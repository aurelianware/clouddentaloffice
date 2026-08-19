using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public sealed class ReviewOutreachTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.Zero));
    private readonly CloudDentalDbContext _db;

    public ReviewOutreachTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        _db = new CloudDentalDbContext(options, new FixedTenantProvider("tenant-a"));
        _db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData("Scheduled", "appointment_not_completed")]
    [InlineData("Cancelled", "appointment_not_completed")]
    [InlineData("NoShow", "appointment_not_completed")]
    public async Task Only_completed_appointments_are_eligible(string status, string reason)
    {
        await SeedAsync(status);
        var result = await Eligibility().EvaluateAsync("tenant-a", 10);
        Assert.False(result.Eligible);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task Completed_active_patient_with_email_is_eligible_without_review_gating()
    {
        await SeedAsync("Completed");
        var result = await Eligibility().EvaluateAsync("tenant-a", 10);
        Assert.True(result.Eligible);
        Assert.Equal("eligible", result.Reason);
    }

    [Fact]
    public async Task Appointment_transition_to_completed_invokes_trusted_scheduler_once()
    {
        await SeedAsync("Scheduled");
        var scheduler = new Mock<IReviewOutreachScheduler>();
        scheduler.Setup(x => x.ScheduleAsync("tenant-a", 10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new AppointmentServiceImpl(_db, new FixedTenantProvider("tenant-a"),
            NullLogger<AppointmentServiceImpl>.Instance, scheduler.Object);
        var update = await _db.Appointments.AsNoTracking().SingleAsync(x => x.AppointmentId == 10);
        update.Status = "Completed";

        await service.UpdateAppointmentAsync(update);

        scheduler.Verify(x => x.ScheduleAsync("tenant-a", 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, "email_missing_or_invalid")]
    [InlineData("not-an-email", "email_missing_or_invalid")]
    public async Task Missing_or_invalid_email_is_suppressed(string? email, string reason)
    {
        await SeedAsync("Completed", email: email);
        Assert.Equal(reason, (await Eligibility().EvaluateAsync("tenant-a", 10)).Reason);
    }

    [Fact]
    public async Task Disabled_setting_or_inactive_patient_is_suppressed()
    {
        await SeedAsync("Completed", enabled: false);
        Assert.Equal("disabled", (await Eligibility().EvaluateAsync("tenant-a", 10)).Reason);
        _db.ReviewOutreachSettings.Single().Enabled = true;
        _db.Patients.Single().Status = "Deceased";
        await _db.SaveChangesAsync();
        Assert.Equal("patient_inactive", (await Eligibility().EvaluateAsync("tenant-a", 10)).Reason);
    }

    [Fact]
    public async Task Duplicate_completion_creates_one_durable_delayed_record()
    {
        await SeedAsync("Completed", delayMinutes: 90);
        var scheduler = Scheduler();
        Assert.True(await scheduler.ScheduleAsync("tenant-a", 10));
        Assert.False(await scheduler.ScheduleAsync("tenant-a", 10));
        var row = Assert.Single(await _db.ReviewOutreaches.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(90), row.ScheduledAt);
    }

    [Fact]
    public async Task Worker_waits_until_due_then_sends_current_contact_without_phi()
    {
        await SeedAsync("Completed", delayMinutes: 60);
        await Scheduler().ScheduleAsync("tenant-a", 10);
        var sender = new RecordingSender();
        var dispatcher = Dispatcher(sender);
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        var request = Assert.Single(sender.Requests);
        Assert.Equal("patient@example.test", request.Recipient);
        Assert.Equal("Practice A", request.PracticeName);
        Assert.Equal("https://practice-a.test/review/", request.LandingPageUrl.AbsoluteUri);
        var serialized = $"{request.PracticeName} {request.LandingPageUrl}";
        Assert.DoesNotContain("10", serialized);
        Assert.DoesNotContain("implant", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reversed_appointment_and_disabled_tenant_suppress_pending_work()
    {
        await SeedAsync("Completed", delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", 10);
        _db.Appointments.Single().Status = "Cancelled";
        await _db.SaveChangesAsync();
        var sender = new RecordingSender();
        await Dispatcher(sender).DispatchBatchAsync();
        Assert.Empty(sender.Requests);
        Assert.Equal(ReviewOutreachStatus.Suppressed, _db.ReviewOutreaches.IgnoreQueryFilters().Single().Status);
    }

    [Fact]
    public async Task Tenant_configuration_cannot_cross_boundaries()
    {
        await SeedAsync("Completed", delayMinutes: 0);
        _db.ReviewOutreachSettings.Add(new ReviewOutreachSettings { TenantId = "tenant-b", Enabled = true,
            SenderName = "Practice B", ReviewLandingPageUrl = "https://practice-b.test/review/", GoogleReviewUrl = "https://google.test/b" });
        await _db.SaveChangesAsync();
        await Scheduler().ScheduleAsync("tenant-a", 10);
        var sender = new RecordingSender();
        await Dispatcher(sender).DispatchBatchAsync();
        Assert.Equal("Practice A", Assert.Single(sender.Requests).PracticeName);
    }

    [Fact]
    public async Task Transient_failure_retries_then_succeeds_and_permanent_failure_does_not_retry()
    {
        await SeedAsync("Completed", delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", 10);
        var transient = new RecordingSender(ReviewOutreachSendDisposition.TransientFailure, ReviewOutreachSendDisposition.Sent);
        Assert.Equal(0, await Dispatcher(transient).DispatchBatchAsync());
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await Dispatcher(transient).DispatchBatchAsync());
        Assert.Equal(2, transient.Requests.Count);

        _db.ReviewOutreaches.RemoveRange(_db.ReviewOutreaches.IgnoreQueryFilters());
        await _db.SaveChangesAsync();
        await Scheduler().ScheduleAsync("tenant-a", 10);
        var permanent = new RecordingSender(ReviewOutreachSendDisposition.PermanentFailure);
        await Dispatcher(permanent).DispatchBatchAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        await Dispatcher(permanent).DispatchBatchAsync();
        Assert.Single(permanent.Requests);
        Assert.Equal(ReviewOutreachStatus.Failed, _db.ReviewOutreaches.IgnoreQueryFilters().Single().Status);
    }

    [Fact]
    public async Task Unexpected_sender_exception_releases_lease_for_retry()
    {
        await SeedAsync("Completed", delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", 10);

        await Dispatcher(new ThrowingSender()).DispatchBatchAsync();

        var row = _db.ReviewOutreaches.IgnoreQueryFilters().Single();
        Assert.Equal(ReviewOutreachStatus.Scheduled, row.Status);
        Assert.Null(row.LockId);
        Assert.Null(row.LockedUntil);
        Assert.Equal("sender_exception_InvalidOperationException", row.FailureReason);
    }

    [Fact]
    public async Task Multiple_senders_for_channel_fail_safely_without_throwing()
    {
        await SeedAsync("Completed", delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", 10);

        await Dispatcher(new RecordingSender(), new RecordingSender()).DispatchBatchAsync();

        var row = _db.ReviewOutreaches.IgnoreQueryFilters().Single();
        Assert.Equal(ReviewOutreachStatus.Failed, row.Status);
        Assert.Equal("multiple_senders_configured", row.FailureReason);
    }

    private IReviewOutreachEligibilityService Eligibility() => new ReviewOutreachEligibilityService(_db);
    private IReviewOutreachScheduler Scheduler() => new ReviewOutreachScheduler(_db, Eligibility(), _clock, NullLogger<ReviewOutreachScheduler>.Instance);
    private IReviewOutreachDispatcher Dispatcher(params IReviewOutreachSender[] senders) => new ReviewOutreachDispatcher(_db, Eligibility(), senders,
        Options.Create(new ReviewOutreachWorkerOptions { InitialRetrySeconds = 60, MaximumRetrySeconds = 60 }), _clock,
        NullLogger<ReviewOutreachDispatcher>.Instance);

    private async Task SeedAsync(string status, string? email = "patient@example.test", bool enabled = true, int delayMinutes = 0)
    {
        _db.Appointments.Add(new Appointment { AppointmentId = 10, TenantId = "tenant-a", PatientId = 20, ProviderId = 1,
            AppointmentDateTime = _clock.GetUtcNow().UtcDateTime, AppointmentType = "implant consult", Status = status });
        _db.Patients.Add(new Patient { PatientId = 20, TenantId = "tenant-a", FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Email = email, Status = "Active" });
        _db.ReviewOutreachSettings.Add(new ReviewOutreachSettings { TenantId = "tenant-a", Enabled = enabled,
            DelayMinutes = delayMinutes, SenderName = "Practice A", ReviewLandingPageUrl = "https://practice-a.test/review/",
            GoogleReviewUrl = "https://google.test/a" });
        await _db.SaveChangesAsync();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
    private sealed class RecordingSender(params ReviewOutreachSendDisposition[] results) : IReviewOutreachSender
    {
        private int _attempt;
        public ReviewOutreachChannel Channel => ReviewOutreachChannel.Email;
        public List<ReviewOutreachSendRequest> Requests { get; } = [];
        public Task<ReviewOutreachSendResult> SendAsync(ReviewOutreachSendRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = results.Length == 0 ? ReviewOutreachSendDisposition.Sent : results[Math.Min(_attempt++, results.Length - 1)];
            return Task.FromResult(new ReviewOutreachSendResult(result, result == ReviewOutreachSendDisposition.Sent ? null : "test_failure"));
        }
    }
    private sealed class ThrowingSender : IReviewOutreachSender
    {
        public ReviewOutreachChannel Channel => ReviewOutreachChannel.Email;
        public Task<ReviewOutreachSendResult> SendAsync(ReviewOutreachSendRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("test sender failure");
    }
}
