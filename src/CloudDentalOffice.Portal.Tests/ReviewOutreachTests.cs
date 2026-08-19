using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;
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
    private static readonly Guid Appointment = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const int PatientId = 20;
    private const string PatientEmail = "patient@example.test";

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

    [Fact]
    public async Task Active_patient_with_email_is_eligible()
    {
        SeedSettings();
        var result = await Eligibility().EvaluateContactAsync("tenant-a", "Active", PatientEmail);
        Assert.True(result.Eligible);
        Assert.Equal("eligible", result.Reason);
    }

    [Theory]
    [InlineData(null, "email_missing_or_invalid")]
    [InlineData("not-an-email", "email_missing_or_invalid")]
    public async Task Missing_or_invalid_email_is_suppressed(string? email, string reason)
    {
        SeedSettings();
        Assert.Equal(reason, (await Eligibility().EvaluateContactAsync("tenant-a", "Active", email)).Reason);
    }

    [Fact]
    public async Task Disabled_setting_or_inactive_patient_is_suppressed()
    {
        SeedSettings(enabled: false);
        Assert.Equal("disabled", (await Eligibility().EvaluateContactAsync("tenant-a", "Active", PatientEmail)).Reason);
        _db.ReviewOutreachSettings.Single().Enabled = true;
        await _db.SaveChangesAsync();
        Assert.Equal("patient_inactive", (await Eligibility().EvaluateContactAsync("tenant-a", "Deceased", PatientEmail)).Reason);
    }

    [Fact]
    public async Task Completing_an_appointment_schedules_outreach_with_the_scheduling_id()
    {
        var scheduler = new Mock<IReviewOutreachScheduler>();
        scheduler.Setup(x => x.ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = HttpAppointmentService(AppointmentStatus.Completed, scheduler.Object);

        await service.UpdateAppointmentAsync(CompletedInput());

        scheduler.Verify(x => x.ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Non_completed_update_does_not_schedule_outreach()
    {
        var scheduler = new Mock<IReviewOutreachScheduler>();
        var service = HttpAppointmentService(AppointmentStatus.Confirmed, scheduler.Object);

        var input = CompletedInput();
        input.Status = "Confirmed";
        await service.UpdateAppointmentAsync(input);

        scheduler.Verify(x => x.ScheduleAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Duplicate_completion_creates_one_durable_delayed_record()
    {
        SeedSettings(delayMinutes: 90);
        var scheduler = Scheduler();
        Assert.True(await scheduler.ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail));
        Assert.False(await scheduler.ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail));
        var row = Assert.Single(await _db.ReviewOutreaches.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(90), row.ScheduledAt);
        Assert.Equal(PatientEmail, row.RecipientEmail);
    }

    [Fact]
    public async Task Worker_waits_until_due_then_sends_snapshot_contact_without_phi()
    {
        SeedSettings(delayMinutes: 60);
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);
        var sender = new RecordingSender();
        var dispatcher = Dispatcher(sender);
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        var request = Assert.Single(sender.Requests);
        Assert.Equal(PatientEmail, request.Recipient);
        Assert.Equal("Practice A", request.PracticeName);
        Assert.Equal("https://practice-a.test/review/", request.LandingPageUrl.AbsoluteUri);
        var serialized = $"{request.PracticeName} {request.LandingPageUrl}";
        Assert.DoesNotContain(Appointment.ToString(), serialized);
        Assert.DoesNotContain(PatientId.ToString(), serialized);
    }

    [Fact]
    public async Task Disabled_tenant_suppresses_pending_work()
    {
        SeedSettings(delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);
        _db.ReviewOutreachSettings.Single().Enabled = false;
        await _db.SaveChangesAsync();
        var sender = new RecordingSender();
        await Dispatcher(sender).DispatchBatchAsync();
        Assert.Empty(sender.Requests);
        Assert.Equal(ReviewOutreachStatus.Suppressed, _db.ReviewOutreaches.IgnoreQueryFilters().Single().Status);
    }

    [Fact]
    public async Task Tenant_configuration_cannot_cross_boundaries()
    {
        SeedSettings(delayMinutes: 0);
        _db.ReviewOutreachSettings.Add(new ReviewOutreachSettings { TenantId = "tenant-b", Enabled = true,
            SenderName = "Practice B", ReviewLandingPageUrl = "https://practice-b.test/review/", GoogleReviewUrl = "https://google.test/b" });
        await _db.SaveChangesAsync();
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);
        var sender = new RecordingSender();
        await Dispatcher(sender).DispatchBatchAsync();
        Assert.Equal("Practice A", Assert.Single(sender.Requests).PracticeName);
    }

    [Fact]
    public async Task Transient_failure_retries_then_succeeds_and_permanent_failure_does_not_retry()
    {
        SeedSettings(delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);
        var transient = new RecordingSender(ReviewOutreachSendDisposition.TransientFailure, ReviewOutreachSendDisposition.Sent);
        Assert.Equal(0, await Dispatcher(transient).DispatchBatchAsync());
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await Dispatcher(transient).DispatchBatchAsync());
        Assert.Equal(2, transient.Requests.Count);

        _db.ReviewOutreaches.RemoveRange(_db.ReviewOutreaches.IgnoreQueryFilters());
        await _db.SaveChangesAsync();
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);
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
        SeedSettings(delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);

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
        SeedSettings(delayMinutes: 0);
        await Scheduler().ScheduleAsync("tenant-a", Appointment, PatientId, "Active", PatientEmail);

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

    private void SeedSettings(bool enabled = true, int delayMinutes = 0)
    {
        _db.ReviewOutreachSettings.Add(new ReviewOutreachSettings { TenantId = "tenant-a", Enabled = enabled,
            DelayMinutes = delayMinutes, SenderName = "Practice A", ReviewLandingPageUrl = "https://practice-a.test/review/",
            GoogleReviewUrl = "https://google.test/a" });
        _db.SaveChanges();
    }

    private AppointmentServiceHttpClient HttpAppointmentService(AppointmentStatus responseStatus, IReviewOutreachScheduler scheduler)
    {
        var dto = new AppointmentDto
        {
            Id = Appointment, PatientId = PatientId, ProviderId = 1,
            StartTime = _clock.GetUtcNow().UtcDateTime, EndTime = _clock.GetUtcNow().UtcDateTime.AddHours(1),
            Status = responseStatus
        };
        var http = new HttpClient(new StubHandler(dto)) { BaseAddress = new Uri("http://gateway.test") };
        var patients = new Mock<IPatientService>();
        patients.Setup(x => x.GetPatientByIdAsync(PatientId.ToString()))
            .ReturnsAsync(new Patient { PatientId = PatientId, Status = "Active", Email = PatientEmail });
        return new AppointmentServiceHttpClient(http, new FixedTenantProvider("tenant-a"), patients.Object,
            NullLogger<AppointmentServiceHttpClient>.Instance, scheduler);
    }

    private Appointment CompletedInput() => new()
    {
        ExternalId = Appointment.ToString(), PatientId = PatientId, ProviderId = 1,
        Status = "Completed", DurationMinutes = 60, AppointmentDateTime = _clock.GetUtcNow().UtcDateTime,
        AppointmentType = "Exam"
    };

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
    private sealed class StubHandler(AppointmentDto response) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, Json), Encoding.UTF8, "application/json")
            });
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
