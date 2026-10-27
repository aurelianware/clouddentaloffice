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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// A website "Check My Insurance" request reaches the front desk, gets matched to a
/// patient, and the patient is emailed the secure coverage link; their answer comes
/// back the same way as for an appointment. Synthetic data only.
/// </summary>
public sealed partial class WebsiteInsuranceCheckTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string Key = "test-only-coverage-intake-signing-key-0123456789";
    private const int PatientId = 301;
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CloudDentalDbContext> _options;
    private readonly RecordingSender _sender = new();
    private readonly CoverageVerificationOptions _verification = new() { TimeZoneId = "UTC" };
    private readonly CoverageIntakeOptions _intake = new()
    {
        Enabled = true, LinkBaseUrl = "https://intake.example.test", SigningKey = Key
    };

    public WebsiteInsuranceCheckTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        using var db = Db();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantRegistry { TenantId = Tenant, Name = "Sunrise Dental" });
        db.Patients.Add(new Patient
        {
            PatientId = PatientId, TenantId = Tenant, FirstName = "Rowan", LastName = "Vale", Email = "rowan@example.test",
            DateOfBirth = new DateTime(1990, 3, 14), Gender = "U", Status = "Active", CreatedDate = Now.UtcDateTime
        });
        db.InsurancePlans.Add(new InsurancePlan { InsurancePlanId = 11, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_relayed_check_is_stored_once_for_its_practice()
    {
        var evt = Event();
        await Record(evt);
        await Record(evt);

        var stored = Assert.Single(await All());
        Assert.Equal(Tenant, stored.TenantId);
        Assert.Equal("Rowan Vale", stored.Name);
        Assert.Equal("Delta Dental", stored.Carrier);
        Assert.Equal("Yes", stored.RequestingAppointment);
        await using var other = new CloudDentalDbContext(_options, new FixedTenantProvider(OtherTenant));
        Assert.Empty(await other.WebsiteInsuranceChecks.ToListAsync());
    }

    [Fact]
    public async Task A_check_without_a_name_or_carrier_is_rejected()
    {
        await Assert.ThrowsAsync<CoverageIntakeRejectedException>(() => Record(Event() with { Carrier = " " }));
        Assert.Empty(await All());
    }

    [Fact]
    public async Task Staff_match_the_visitor_and_email_the_secure_link()
    {
        var evt = Event();
        await Record(evt);
        var service = Service();
        Assert.Equal(WebsiteInsuranceCheckStep.MatchPatient, Assert.Single(await service.GetOpenAsync()).Step);

        await service.MatchPatientAsync(evt.EventId, PatientId);
        var item = Assert.Single(await service.GetOpenAsync());
        Assert.Equal(WebsiteInsuranceCheckStep.SendLink, item.Step);
        Assert.Equal("Rowan Vale", item.PatientName);

        var request = await service.SendLinkAsync(evt.EventId);

        var message = Assert.Single(_sender.Messages);
        Assert.Equal("rowan@example.test", message.Recipient);
        Assert.Contains("Thank you for asking Sunrise Dental to check your dental insurance.", message.Body);
        Assert.DoesNotContain("appointment", message.Body);
        Assert.True(CoverageIntakeToken.TryRead(TokenIn(message.Body), Key, Now, out var ticket));
        Assert.Equal(request.Id, ticket.RequestId);
        Assert.Equal(Tenant, ticket.TenantId);
        Assert.Null(request.CoverageVerificationId);
        Assert.Equal(CoverageIntakeStatus.Sent, request.Status);
        Assert.Equal(WebsiteInsuranceCheckStep.WaitingForPatient, Assert.Single(await service.GetOpenAsync()).Step);

        // Sending again while the link is out doesn't email the patient twice.
        Assert.Equal(request.Id, (await service.SendLinkAsync(evt.EventId)).Id);
        Assert.Single(_sender.Messages);
    }

    [Fact]
    public async Task The_patients_answer_and_their_coverage_move_the_check_along()
    {
        var evt = Event();
        await Record(evt);
        var service = Service();
        await service.MatchPatientAsync(evt.EventId, PatientId);
        await service.SendLinkAsync(evt.EventId);
        var token = TokenIn(_sender.Messages[0].Body);
        Assert.True(CoverageIntakeToken.TryRead(token, Key, Now, out var ticket));

        await using (var db = new CloudDentalDbContext(_options))
        {
            await new CoverageIntakeProcessor(db, Options.Create(_intake), Options.Create(_verification), new Clock(Now),
                NullLogger<CoverageIntakeProcessor>.Instance).ProcessAsync(
                new CoverageIntakeSubmittedEvent(Tenant, ticket.RequestId, token, CoverageIntakeSubmittedEvent.Plan)
                {
                    SubmittedAtUtc = Now.UtcDateTime, CarrierName = "Delta Dental", MemberId = "MBR-123", RelationshipToSubscriber = "Self"
                });
        }
        var item = Assert.Single(await service.GetOpenAsync());
        Assert.Equal(WebsiteInsuranceCheckStep.AddCoverage, item.Step);
        Assert.Equal(ticket.RequestId, item.SentPlanId);

        await using (var db = Db())
        {
            db.PatientInsurances.Add(new PatientInsurance
            {
                TenantId = Tenant, PatientId = PatientId, InsurancePlanId = 11, MemberId = "MBR-123", SequenceNumber = 1,
                EffectiveDate = Now.UtcDateTime.AddYears(-1)
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(WebsiteInsuranceCheckStep.CoverageOnFile, Assert.Single(await service.GetOpenAsync()).Step);

        await service.CloseAsync(evt.EventId);
        Assert.Empty(await service.GetOpenAsync());
        Assert.NotNull(Assert.Single(await All()).ClosedAt);
    }

    [Fact]
    public async Task A_patient_without_an_email_is_called_instead()
    {
        await using (var db = Db())
        {
            (await db.Patients.SingleAsync()).Email = null;
            await db.SaveChangesAsync();
        }
        var evt = Event();
        await Record(evt);
        var service = Service();
        await service.MatchPatientAsync(evt.EventId, PatientId);

        Assert.Equal(WebsiteInsuranceCheckStep.CallPatient, Assert.Single(await service.GetOpenAsync()).Step);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendLinkAsync(evt.EventId));
        Assert.Empty(_sender.Messages);
    }

    [Fact]
    public async Task Only_practice_staff_can_work_the_list()
    {
        var service = Service(new ClaimsPrincipal(new ClaimsIdentity()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetOpenAsync());
    }

    [Theory]
    [InlineData(false, false, false, null, WebsiteInsuranceCheckStep.MatchPatient)]
    [InlineData(true, false, false, null, WebsiteInsuranceCheckStep.CallPatient)]
    [InlineData(true, true, false, null, WebsiteInsuranceCheckStep.SendLink)]
    [InlineData(true, true, false, CoverageIntakeStatus.Failed, WebsiteInsuranceCheckStep.LinkFailed)]
    [InlineData(true, true, true, CoverageIntakeStatus.Sent, WebsiteInsuranceCheckStep.CoverageOnFile)]
    public void Each_check_shows_the_next_step(bool matched, bool hasEmail, bool hasCoverage, CoverageIntakeStatus? status,
        WebsiteInsuranceCheckStep expected)
    {
        var request = status is { } s ? new CoverageIntakeRequest { Status = s } : null;
        Assert.Equal(expected, WebsiteInsuranceCheckService.StepFor(matched, hasEmail, hasCoverage, request));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static WebsiteInsuranceCheckEvent Event() => new(Tenant, "Rowan Vale", "Delta Dental")
    {
        EventId = Guid.Parse("4b1f0c9e-2a7d-4e3b-8c5f-1d2e3f4a5b6c"),
        Email = "rowan@example.test", Phone = "480-555-0100", RequestingAppointment = "Yes",
        SubmittedAtUtc = Now.UtcDateTime.AddMinutes(-1)
    };

    private async Task Record(WebsiteInsuranceCheckEvent evt)
    {
        await using var db = new CloudDentalDbContext(_options);
        await WebsiteInsuranceCheckService.RecordAsync(db, evt, Now.UtcDateTime);
    }

    private async Task<List<WebsiteInsuranceCheck>> All()
    {
        await using var db = new CloudDentalDbContext(_options);
        return await db.WebsiteInsuranceChecks.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    private WebsiteInsuranceCheckService Service(ClaimsPrincipal? user = null)
    {
        user ??= new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, "Staff"), new System.Security.Claims.Claim(ClaimTypes.Name, "front@sunrise.test")], "test"));
        var tenant = new FixedTenantProvider(Tenant, user);
        var db = new CloudDentalDbContext(_options, tenant);
        var intake = new CoverageIntakeService(db, _sender, tenant, Options.Create(_intake), Options.Create(_verification),
            new Clock(Now), NullLogger<CoverageIntakeService>.Instance);
        return new WebsiteInsuranceCheckService(db, intake, tenant, new Clock(Now));
    }

    private CloudDentalDbContext Db() => new(_options, new FixedTenantProvider(Tenant));

    private static string TokenIn(string body) => LinkPattern().Match(body).Groups[1].Value;

    [GeneratedRegex(@"https://intake\.example\.test/coverage/(\S+)")]
    private static partial Regex LinkPattern();

    private sealed class RecordingSender : IPatientBillingNotificationSender
    {
        public List<BillingNotificationMessage> Messages { get; } = [];

        public Task<BillingNotificationSendResult> SendAsync(BillingNotificationMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(new BillingNotificationSendResult(BillingNotificationSendDisposition.Sent));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user = null) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => user;
    }
}
