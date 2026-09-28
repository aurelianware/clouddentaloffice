using System.Security.Claims;
using System.Text.RegularExpressions;
using CloudDentalOffice.Contracts.Eligibility;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedStatus = CloudDentalOffice.Contracts.Scheduling.AppointmentStatus;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Patients with no coverage on file get a signed link, one reminder, and their
/// answer comes back to the portal: a typed plan waits for staff, "no dental
/// insurance" makes their appointments self-pay. Synthetic data only.
/// </summary>
public sealed partial class CoverageIntakeTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string Key = "test-only-coverage-intake-signing-key-0123456789";
    private const int PatientId = 201;
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CloudDentalDbContext> _options;
    private readonly MutableClock _clock = new(Start);
    private readonly FakeFeed _feed = new();
    private readonly RecordingSender _sender = new();
    private readonly CoverageVerificationOptions _verification = new() { Enabled = true, TimeZoneId = "UTC" };
    private readonly CoverageIntakeOptions _intake = new()
    {
        Enabled = true, LinkBaseUrl = "https://intake.example.test", SigningKey = Key
    };

    public CoverageIntakeTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        using var db = Db();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantRegistry { TenantId = Tenant, Name = "Sunrise Dental" });
        db.Patients.Add(new Patient
        {
            PatientId = PatientId, TenantId = Tenant, FirstName = "Rowan", LastName = "Vale", Email = "rowan@example.test",
            DateOfBirth = new DateTime(1990, 3, 14), Gender = "U", Status = "Active", CreatedDate = Start.UtcDateTime
        });
        db.InsurancePlans.Add(new InsurancePlan { InsurancePlanId = 11, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true });
        db.Providers.Add(new Provider { ProviderId = 7, TenantId = Tenant, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── Sending ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_patient_without_coverage_is_emailed_a_link_for_this_request()
    {
        var appointment = _feed.Add(Start.AddDays(5));

        await Run();

        var message = Assert.Single(_sender.Messages);
        Assert.Equal("rowan@example.test", message.Recipient);
        Assert.Contains("Sunrise Dental", message.Body);
        Assert.Contains("Saturday, October 10", message.Body);
        var request = await Request();
        Assert.Equal(CoverageIntakeStatus.Sent, request.Status);
        Assert.True(CoverageIntakeToken.TryRead(TokenIn(message.Body), Key, Start, out var ticket));
        Assert.Equal(request.Id, ticket.RequestId);
        Assert.Equal(Tenant, ticket.TenantId);
        Assert.Contains("Asked the patient by email", (await Row(appointment)).Reason);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await Run();
        Assert.Single(_sender.Messages);
    }

    [Fact]
    public async Task One_reminder_goes_out_before_the_visit()
    {
        _feed.Add(Start.AddDays(5));
        await Run();

        _clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1)); // just inside 48h
        await Run();
        _clock.Advance(TimeSpan.FromHours(1));
        await Run();

        Assert.Equal(2, _sender.Messages.Count);
        Assert.StartsWith("Reminder:", _sender.Messages[1].Subject);
        Assert.NotNull((await Request()).ReminderSentAt);
    }

    [Fact]
    public async Task No_email_on_file_means_staff_call_instead()
    {
        await using (var db = Db())
        {
            (await db.Patients.SingleAsync()).Email = null;
            await db.SaveChangesAsync();
        }
        var appointment = _feed.Add(Start.AddDays(5));

        await Run();

        Assert.Empty(_sender.Messages);
        Assert.Equal(CoverageIntakeRules.NoEmailReason, (await Row(appointment)).Reason);
    }

    [Fact]
    public async Task Failed_delivery_is_retried_then_left_to_staff()
    {
        _sender.Result = new(BillingNotificationSendDisposition.TransientFailure, "SmtpException");
        var appointment = _feed.Add(Start.AddDays(5));

        for (var i = 0; i < 4; i++)
        {
            await Run();
            _clock.Advance(TimeSpan.FromMinutes(15));
        }

        Assert.Equal(_intake.MaxSendAttempts, _sender.Messages.Count);
        Assert.Equal(CoverageIntakeStatus.Failed, (await Request()).Status);
        Assert.Contains("couldn't be delivered", (await Row(appointment)).Reason);
    }

    [Fact]
    public async Task Nothing_is_sent_when_intake_is_not_configured()
    {
        _intake.SigningKey = "short";
        var appointment = _feed.Add(Start.AddDays(5));

        await Run();

        Assert.Empty(_sender.Messages);
        Assert.Equal(CoverageVerificationSweep.NoCoverageReason, (await Row(appointment)).Reason);
    }

    // ── Answers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_insurance_makes_the_appointments_self_pay_until_coverage_is_added()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        await Run();

        await Answer(Answer(CoverageIntakeSubmittedEvent.NoInsurance));

        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.SelfPay, row.State);
        Assert.Contains("no dental insurance", row.Reason);
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Run();
        Assert.Equal(EligibilityVerificationState.SelfPay, (await Row(appointment)).State);
        Assert.Single(_sender.Messages);
        Assert.Empty(await Queue().GetAsync(7, includeVerified: false));

        // Staff later add coverage: the appointment is checked like any other.
        await using (var db = Db())
        {
            db.PatientInsurances.Add(new PatientInsurance
            {
                PatientInsuranceId = 901, TenantId = Tenant, PatientId = PatientId, InsurancePlanId = 11, MemberId = "MBR9",
                SequenceNumber = 1, RelationshipToSubscriber = "Self", EffectiveDate = new DateTime(2026, 1, 1), IsActive = true
            });
            await db.SaveChangesAsync();
        }
        await Run();
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(appointment)).State);
    }

    [Fact]
    public async Task A_typed_plan_waits_for_staff_and_stops_further_emails()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        await Run();

        await Answer(Answer(CoverageIntakeSubmittedEvent.Plan) with
        {
            CarrierName = "Delta Dental", MemberId = "MBR-555", RelationshipToSubscriber = "Self"
        });

        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.NeedsInfo, row.State);
        Assert.Contains("Delta Dental", row.Reason);
        Assert.DoesNotContain("MBR-555", row.Reason);

        _clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await Run();
        Assert.Single(_sender.Messages); // no reminder after an answer
        Assert.Contains("Delta Dental", (await Row(appointment)).Reason);

        var item = Assert.Single(await Queue().GetAsync(7, includeVerified: false));
        var sent = await Queue().GetSentPlanAsync(item.SentPlanId!.Value);
        Assert.Equal("MBR-555", sent!.MemberId);
    }

    [Fact]
    public async Task The_first_answer_wins()
    {
        _feed.Add(Start.AddDays(5));
        await Run();

        await Answer(Answer(CoverageIntakeSubmittedEvent.NoInsurance));
        await Answer(Answer(CoverageIntakeSubmittedEvent.Plan) with { CarrierName = "Other", MemberId = "X1", RelationshipToSubscriber = "Self" });

        Assert.Equal(CoverageIntakeAnswer.NoInsurance, (await Request()).Answer);
    }

    [Fact]
    public async Task Forged_or_mismatched_answers_are_rejected()
    {
        _feed.Add(Start.AddDays(5));
        await Run();
        var good = Answer(CoverageIntakeSubmittedEvent.NoInsurance);
        var otherKey = CoverageIntakeToken.Create(new(Tenant, good.RequestId, "x", Start.AddDays(7)), Key.Replace('0', '9'));
        var otherRequest = CoverageIntakeToken.Create(new(Tenant, Guid.NewGuid(), "x", Start.AddDays(7)), Key);

        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Answer(good with { Token = otherKey }));
        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Answer(good with { Token = otherRequest }));
        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Answer(good with { TenantId = "tenant-b" }));
        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Answer(good with { SubmittedAtUtc = Start.UtcDateTime.AddDays(-20) }));
        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Answer(Answer(CoverageIntakeSubmittedEvent.Plan)));
        Assert.Null((await Request()).Answer);
    }

    [Fact]
    public async Task An_answer_sent_before_the_link_expired_is_accepted_after_it()
    {
        _feed.Add(Start.AddDays(10));
        await Run();
        var answer = Answer(CoverageIntakeSubmittedEvent.NoInsurance) with { SubmittedAtUtc = Start.UtcDateTime.AddDays(6) };

        _clock.Advance(TimeSpan.FromDays(8)); // processed after the 7-day link expired
        await Answer(answer);

        Assert.Equal(CoverageIntakeAnswer.NoInsurance, (await Request()).Answer);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task Run()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        PinnedTenantScope.Register<DefaultTenantProvider>(services);
        services.AddScoped(sp => new CloudDentalDbContext(_options, sp.GetRequiredService<ITenantProvider>()));
        services.AddSingleton(_options);
        services.AddSingleton<IScheduledAppointmentFeed>(_feed);
        services.AddSingleton<IPayerTransactionRouter>(new ActiveRouter());
        services.AddSingleton<IPatientBillingNotificationSender>(_sender);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(Options.Create(_verification));
        services.AddSingleton(Options.Create(_intake));
        services.AddScoped<IEligibilityVerificationService, EligibilityVerificationService>();
        services.AddScoped<ICoverageVerificationSweep, CoverageVerificationSweep>();
        services.AddScoped<ICoverageIntakeService, CoverageIntakeService>();
        await using var provider = services.BuildServiceProvider();
        await using (var db = Db())
        {
            if (!await db.TenantClearinghouseConnections.AnyAsync())
            {
                db.TenantClearinghouseConnections.Add(new TenantClearinghouseConnection
                {
                    TenantId = Tenant, Status = ClearinghouseConnectionStatus.Active, KeyReference = $"stedi-apikey-{Tenant}"
                });
                await db.SaveChangesAsync();
            }
        }
        await new CoverageVerificationRunner(provider, NullLogger<CoverageVerificationRunner>.Instance).RunOnceAsync();
    }

    private async Task Answer(CoverageIntakeSubmittedEvent answer)
    {
        await using var db = new CloudDentalDbContext(_options);
        await new CoverageIntakeProcessor(db, Options.Create(_intake), Options.Create(_verification), _clock,
            NullLogger<CoverageIntakeProcessor>.Instance).ProcessAsync(answer);
    }

    private CoverageIntakeSubmittedEvent Answer(string kind)
    {
        var token = TokenIn(_sender.Messages[0].Body);
        Assert.True(CoverageIntakeToken.TryRead(token, Key, Start, out var ticket));
        return new(ticket.TenantId, ticket.RequestId, token, kind) { SubmittedAtUtc = _clock.GetUtcNow().UtcDateTime };
    }

    private CoverageVerificationQueue Queue() => new(Db(), new ServiceCollection().BuildServiceProvider(),
        new FixedTenantProvider(Tenant, new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, "Staff")], "test"))),
        Options.Create(_verification), _clock);

    private async Task<CoverageIntakeRequest> Request()
    {
        await using var db = Db();
        return await db.CoverageIntakeRequests.AsNoTracking().SingleAsync();
    }

    private async Task<CoverageVerification> Row(Guid appointmentId)
    {
        await using var db = Db();
        return await db.CoverageVerifications.AsNoTracking().SingleAsync(x => x.AppointmentId == appointmentId);
    }

    private CloudDentalDbContext Db() => new(_options, new FixedTenantProvider(Tenant));

    private static string TokenIn(string body) => LinkPattern().Match(body).Groups[1].Value;

    [GeneratedRegex(@"https://intake\.example\.test/coverage/(\S+)")]
    private static partial Regex LinkPattern();

    private sealed class FakeFeed : IScheduledAppointmentFeed
    {
        private readonly List<ScheduledAppointment> _appointments = [];

        public Guid Add(DateTimeOffset start)
        {
            var id = Guid.NewGuid();
            _appointments.Add(new(id, PatientId, 7, start.UtcDateTime, SchedStatus.Scheduled));
            return id;
        }

        public Task<ScheduledAppointmentWindow> GetAsync(string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new ScheduledAppointmentWindow(
                _appointments.Where(a => a.StartUtc >= fromUtc && a.StartUtc <= toUtc).ToList(), false));
    }

    private sealed class RecordingSender : IPatientBillingNotificationSender
    {
        public BillingNotificationSendResult Result { get; set; } = new(BillingNotificationSendDisposition.Sent);
        public List<BillingNotificationMessage> Messages { get; } = [];

        public Task<BillingNotificationSendResult> SendAsync(BillingNotificationMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }
    }

    private sealed class ActiveRouter : IPayerTransactionRouter
    {
        public Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EligibilityResult { CorrelationId = "c", CoverageStatus = CoverageStatus.Active, Source = "Clearinghouse" });

        public Task<RoutedTreatmentEstimate> GetEstimateAsync(PayerEstimateRoutingRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user = null) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => user;
    }
}
