using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using ClaimEntity = CloudDentalOffice.Portal.Models.Claim;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public sealed class ClaimIntelligenceClientTests
{
    [Fact]
    public async Task GetAsync_SendsTenantHeaderAndRelativeIntelligencePath()
    {
        HttpRequestMessage? captured = null;
        var client = Client(request =>
        {
            captured = request;
            return Task.FromResult(Json(HttpStatusCode.OK, PaidJson()));
        });

        var view = await client.GetAsync("tenant-a", "cho-123");

        Assert.NotNull(view);
        Assert.Equal("Paid", view!.LifecycleStatus);
        Assert.Equal("cho-123", view.ClaimId);
        Assert.Equal(80m, view.Financial.PatientResponsibility);
        Assert.Equal("https://cloudhealthoffice.example/api/claims/cho-123/intelligence", captured!.RequestUri!.ToString());
        Assert.Equal("tenant-a", captured.Headers.GetValues("X-Tenant-ID").Single());
        Assert.Equal("GET", captured.Method.Method);
    }

    [Fact]
    public async Task GetAsync_UsesIntelligenceBaseUrlWhenConfigured()
    {
        HttpRequestMessage? captured = null;
        var options = Options.Create(new CloudHealthOfficeOptions
        {
            Enabled = true,
            BaseUrl = "https://claims.example",
            IntelligenceBaseUrl = "https://intelligence.example",
            IntelligencePath = "/api/claims/{claimId}/intelligence"
        });
        var client = new ClaimIntelligenceClient(new HttpClient(new Handler(request =>
        {
            captured = request;
            return Task.FromResult(Json(HttpStatusCode.OK, PaidJson()));
        })), options, NullLogger<ClaimIntelligenceClient>.Instance);

        await client.GetAsync("tenant-a", "cho-123");

        Assert.Equal("https://intelligence.example/api/claims/cho-123/intelligence", captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnNotFound()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await client.GetAsync("tenant-a", "missing"));
    }

    [Fact]
    public async Task GetAsync_RejectsPathInjectionInClaimId()
    {
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, PaidJson())));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync("tenant-a", "../other"));
    }

    [Fact]
    public async Task GetAsync_MapsServerErrorsToUnavailable()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<ClaimIntelligenceUnavailableException>(() => client.GetAsync("tenant-a", "cho-123"));
    }

    private static ClaimIntelligenceClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        var options = Options.Create(new CloudHealthOfficeOptions
        {
            Enabled = true,
            BaseUrl = "https://cloudhealthoffice.example",
            IntelligencePath = "/api/claims/{claimId}/intelligence"
        });
        return new ClaimIntelligenceClient(new HttpClient(new Handler(send)), options,
            NullLogger<ClaimIntelligenceClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    internal static string PaidJson() => """
        {
          "claimId":"cho-123",
          "tenantId":"tenant-a",
          "lifecycleStatus":"Paid",
          "financial":{"submittedAmount":500,"allowedAmount":400,"paidAmount":320,"patientResponsibility":80,"hasRemittance":true},
          "workflow":{"expected":"Ready for posting","nextAction":"ReadyForPosting"},
          "signals":{"actionRequired":false,"needsFollowUp":false},
          "timeline":[
            {"eventId":"837:tx-1","timestamp":"2026-08-01T12:00:00Z","eventType":"GatewayAccepted","sourceTransaction":"837","status":"SubmissionAcceptedByGateway"},
            {"eventId":"277ca:ack-1","timestamp":"2026-08-02T12:00:00Z","eventType":"277CAAccepted","sourceTransaction":"277CA","status":"Accepted"},
            {"eventId":"276:inq-1","timestamp":"2026-08-03T12:00:00Z","eventType":"276277InProcess","sourceTransaction":"276277","status":"InProcess"},
            {"eventId":"835:r-1","timestamp":"2026-08-10T12:00:00Z","eventType":"ReadyForPosting","sourceTransaction":"835","status":"AvailableForPosting"}
          ],
          "generatedAtUtc":"2026-08-10T12:05:00Z"
        }
        """;
}

public sealed class ClaimLifecycleMapperTests
{
    [Fact]
    public void Timeline_UsesPracticeLanguage_WithoutEdiOrVendors()
    {
        var wire = SamplePaid();
        var claim = new ClaimEntity { ClaimId = 1, ClaimNumber = "CLM-1", TenantId = "tenant-a" };
        var view = ClaimLifecycleMapper.ToView(claim, wire, []);

        Assert.Equal("Paid", view.Status);
        Assert.Equal("Post payment to the patient account", view.NextAction);
        Assert.Equal(80m, view.PatientResponsibility);
        Assert.Equal(["Submission accepted", "Payer accepted the claim", "Payer is processing the claim", "Payment ready to post"],
            view.Timeline.Select(e => e.Title).ToArray());
        Assert.All(view.Timeline, e => Assert.False(ContainsForbidden(e.Title)));
        Assert.All(view.Timeline, e => Assert.False(ContainsForbidden(e.Detail)));
        Assert.False(ContainsForbidden(view.Status));
        Assert.False(ContainsForbidden(view.NextAction));
        Assert.False(ContainsForbidden(view.Expected));
    }

    [Fact]
    public void PostedRemittance_MapsToPostedWithoutEdi()
    {
        var evt = ClaimLifecycleMapper.MapEvent(new ClaimIntelligenceWireEvent
        {
            EventId = "835:r-1",
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "Posted",
            SourceTransaction = "835",
            Status = "Posted"
        });
        Assert.Equal("Payment posted", evt.Title);
        Assert.Equal("Posted", evt.Detail);
        Assert.False(ContainsForbidden(evt.Title));
    }

    [Fact]
    public void ContractualAdjustment_UsesAllowedWhenPresent()
    {
        var financial = new ClaimIntelligenceWireFinancial
        {
            SubmittedAmount = 500m, AllowedAmount = 400m, PaidAmount = 320m,
            PatientResponsibility = 80m, HasRemittance = true
        };
        Assert.Equal(100m, ClaimLifecycleMapper.ContractualAdjustment(financial, 500m, 320m));
    }

    [Fact]
    public void ContractualAdjustment_FallsBackToPatientRemainder()
    {
        var financial = new ClaimIntelligenceWireFinancial
        {
            SubmittedAmount = 500m, PaidAmount = 320m, PatientResponsibility = 80m, HasRemittance = true
        };
        Assert.Equal(100m, ClaimLifecycleMapper.ContractualAdjustment(financial, 500m, 320m));
    }

    [Fact]
    public void ContractualAdjustment_IsZeroWhenFullyAllowed()
    {
        var financial = new ClaimIntelligenceWireFinancial
        {
            SubmittedAmount = 500m, AllowedAmount = 500m, PaidAmount = 400m,
            PatientResponsibility = 100m, HasRemittance = true
        };
        Assert.Equal(0m, ClaimLifecycleMapper.ContractualAdjustment(financial, 500m, 400m));
    }

    [Fact]
    public void DeniedRemittance_IsNotPosted()
    {
        var wire = SamplePaid();
        wire.LifecycleStatus = "Denied";
        Assert.False(ClaimLifecycleMapper.ShouldPostFinancials(wire));
    }

    private static ClaimIntelligenceWireView SamplePaid() =>
        System.Text.Json.JsonSerializer.Deserialize<ClaimIntelligenceWireView>(
            ClaimIntelligenceClientTests.PaidJson(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    private static bool ContainsForbidden(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] tokens = ["837", "277CA", "276", "275", "835", "X12", "EDI", "Stedi", "Availity"];
        return tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ClaimLifecycleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly PatientAccountService _accounts;
    private readonly FixedTenantProvider _tenant = new("tenant-a");
    private readonly DateTime _now = new(2026, 8, 10, 12, 5, 0, DateTimeKind.Utc);

    public ClaimLifecycleServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        _db = new CloudDentalDbContext(options, _tenant);
        _db.Database.EnsureCreated();
        _db.Patients.Add(new Patient
        {
            TenantId = "tenant-a", PatientId = 101, FirstName = "Jamie", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active"
        });
        _db.SaveChanges();
        _accounts = new PatientAccountService(_db, TimeProvider.System, _tenant, NullLogger<PatientAccountService>.Instance);
    }

    [Fact]
    public async Task Refresh_PaidRemittance_UpdatesClaimAndPostsPatientLedger()
    {
        var claim = SeedClaim("cho-123", "Submitted");
        var service = Service(PaidWire());

        var view = await service.RefreshAsync(claim.ClaimId);

        Assert.NotNull(view);
        Assert.Equal("Paid", view!.StatusCode);
        Assert.Equal(320m, view.PaidAmount);
        Assert.Equal(80m, view.PatientResponsibility);
        Assert.True(view.FinancialsPosted);
        Assert.Equal(["Billed amount", "Insurance payment", "Contractual adjustment"],
            view.PostedFinancials.Select(p => p.Description).ToArray());
        Assert.Equal([500m, 320m, 100m], view.PostedFinancials.Select(p => p.Amount).ToArray());

        var stored = _db.Claims.Single(c => c.ClaimId == claim.ClaimId);
        Assert.Equal("Paid", stored.Status);
        Assert.Equal("Paid", stored.LifecycleStatus);
        Assert.Equal(320m, stored.PaidAmount);
        Assert.Equal(80m, stored.PatientResponsibility);
        Assert.NotNull(stored.FinancialsPostedAt);

        var balance = (await _accounts.GetSummaryAsync("tenant-a", 101))!.Balance;
        Assert.Equal(80m, balance.AmountDue);
    }

    [Fact]
    public async Task Refresh_IsIdempotent_ForPostedFinancials()
    {
        var claim = SeedClaim("cho-123", "Submitted");
        var service = Service(PaidWire());
        await service.RefreshAsync(claim.ClaimId);
        await service.RefreshAsync(claim.ClaimId);

        Assert.Equal(3, _db.PatientLedgerEntries.Count());
        Assert.Equal(80m, (await _accounts.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
    }

    [Fact]
    public async Task Refresh_DoesNotCallCho_ForUnsubmittedDraft()
    {
        var claim = SeedClaim(null, "Draft");
        var client = new Mock<IClaimIntelligenceClient>(MockBehavior.Strict);
        var service = new ClaimLifecycleService(_db, client.Object, _accounts, _tenant,
            new FrozenTime(_now), NullLogger<ClaimLifecycleService>.Instance);

        var view = await service.RefreshAsync(claim.ClaimId);

        Assert.Equal("Draft", view!.StatusCode);
        client.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_db.PatientLedgerEntries);
    }

    [Fact]
    public async Task Refresh_RejectsCrossTenantClaim()
    {
        var claim = SeedClaim("cho-123", "Submitted");
        claim.TenantId = "other-tenant";
        _db.SaveChanges();
        var service = Service(PaidWire());

        Assert.Null(await service.RefreshAsync(claim.ClaimId));
        Assert.Empty(_db.PatientLedgerEntries);
    }

    [Fact]
    public async Task Refresh_FallsBackToLocalStatus_WhenChoUnavailable()
    {
        var claim = SeedClaim("cho-123", "Submitted");
        var client = new Mock<IClaimIntelligenceClient>();
        client.Setup(c => c.GetAsync("tenant-a", "cho-123", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClaimIntelligenceUnavailableException("down"));
        var service = new ClaimLifecycleService(_db, client.Object, _accounts, _tenant,
            new FrozenTime(_now), NullLogger<ClaimLifecycleService>.Instance);

        var view = await service.RefreshAsync(claim.ClaimId);

        Assert.Equal("Submitted", view!.StatusCode);
        Assert.Empty(_db.PatientLedgerEntries);
    }

    [Fact]
    public void Presentation_OmitsRemittanceMetadata()
    {
        var wire = PaidWire();
        wire.Timeline[1].Metadata = "MEMBER-SECRET";
        var view = ClaimLifecycleMapper.ToView(new ClaimEntity { ClaimId = 1, ClaimNumber = "CLM-1" }, wire, []);
        Assert.DoesNotContain("MEMBER-SECRET", view.Timeline.Select(e => e.Detail + e.Title));
    }

    private ClaimEntity SeedClaim(string? choId, string status)
    {
        var claim = new ClaimEntity
        {
            TenantId = "tenant-a",
            ClaimNumber = "CLM-2026-0001",
            PatientId = 101,
            ProviderId = 1,
            Status = status,
            ClaimType = "Primary",
            ServiceDateFrom = _now.AddDays(-20),
            TotalChargeAmount = 500m,
            CloudHealthOfficeClaimId = choId,
            SubmittedDate = status == "Draft" ? null : _now.AddDays(-10),
            CreatedDate = _now.AddDays(-12)
        };
        _db.Claims.Add(claim);
        _db.SaveChanges();
        return claim;
    }

    private ClaimLifecycleService Service(ClaimIntelligenceWireView wire)
    {
        var client = new Mock<IClaimIntelligenceClient>();
        client.Setup(c => c.GetAsync("tenant-a", "cho-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(wire);
        return new ClaimLifecycleService(_db, client.Object, _accounts, _tenant,
            new FrozenTime(_now), NullLogger<ClaimLifecycleService>.Instance);
    }

    private static ClaimIntelligenceWireView PaidWire() =>
        System.Text.Json.JsonSerializer.Deserialize<ClaimIntelligenceWireView>(
            ClaimIntelligenceClientTests.PaidJson(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }

    private sealed class FrozenTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
