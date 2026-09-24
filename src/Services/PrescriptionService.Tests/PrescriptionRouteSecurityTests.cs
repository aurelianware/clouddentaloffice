using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using PrescriptionService.Domain;

public sealed class PrescriptionRouteSecurityTests : IClassFixture<PrescriptionSecurityFactory>
{
    private static readonly Guid AnyId = Guid.NewGuid();
    private readonly PrescriptionSecurityFactory _factory;

    public PrescriptionRouteSecurityTests(PrescriptionSecurityFactory factory) => _factory = factory;

    public static TheoryData<string, string> StaffRoutes() => new()
    {
        { "POST", "/api/prescriptions" },
        { "GET", $"/api/prescriptions/{AnyId}" },
        { "GET", $"/api/prescriptions/patient/{AnyId}" },
        { "POST", $"/api/prescriptions/{AnyId}/send" },
        { "POST", $"/api/prescriptions/{AnyId}/cancel?reason=x" },
        { "POST", $"/api/prescriptions/{AnyId}/refill-response" },
        { "POST", $"/api/prescriptions/check-interactions?patientId={AnyId}&rxNormCode=1" },
        { "POST", "/api/prescriptions/check-benefits" },
        { "GET", $"/api/prescriptions/patient/{AnyId}/medication-history" },
        { "GET", $"/api/prescriptions/sso-url?providerId={AnyId}" },
        { "GET", "/api/prescriptions/notifications/clinician-1" },
        { "POST", "/api/prescribers" },
        { "GET", $"/api/prescribers/{AnyId}" },
        { "GET", $"/api/prescribers/{AnyId}/epcs-status" },
        { "GET", "/api/pharmacies/search?zipCode=78701" },
        { "GET", $"/api/patients/{AnyId}/allergies" },
        { "POST", $"/api/patients/{AnyId}/allergies" }
    };

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    public async Task Routes_reject_requests_without_a_token(string method, string route)
    {
        var response = await _factory.CreateClient().SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    public async Task Routes_forbid_patient_portal_tokens_and_tokens_without_tenant(string method, string route)
    {
        var patient = _factory.CreateClient();
        patient.DefaultRequestHeaders.Authorization = new("Bearer", PrescriptionSecurityFactory.Token("tenant-a", role: "Patient"));
        var noTenant = _factory.CreateClient();
        noTenant.DefaultRequestHeaders.Authorization = new("Bearer", PrescriptionSecurityFactory.Token(tenantId: null));

        Assert.Equal(HttpStatusCode.Forbidden, (await patient.SendAsync(Request(method, route))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noTenant.SendAsync(Request(method, route))).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_prescription_reads_and_writes_return_404_and_leave_row_unchanged()
    {
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.ClientFor("tenant-a");

        var get = await client.GetAsync($"/api/prescriptions/{b.PrescriptionId}");
        var send = await client.PostAsJsonAsync($"/api/prescriptions/{b.PrescriptionId}/send", new { PrescriptionId = b.PrescriptionId });
        var cancel = await client.PostAsync($"/api/prescriptions/{b.PrescriptionId}/cancel?reason=hijack", null);
        var refill = await client.PostAsJsonAsync($"/api/prescriptions/{b.PrescriptionId}/refill-response",
            new { PrescriptionId = b.PrescriptionId, Approved = true, NewRefillCount = 9 });
        var list = await client.GetFromJsonAsync<List<IdOnly>>($"/api/prescriptions/patient/{b.PatientId}?includeExpired=true");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refill.StatusCode);
        Assert.Empty(list!);
        var row = await _factory.PrescriptionAsync(b.PrescriptionId);
        Assert.Equal("Draft", row.Status);
        Assert.Empty(row.AuditTrail);
    }

    [Fact]
    public async Task Cross_tenant_prescriber_and_allergy_routes_return_404_or_nothing()
    {
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.ClientFor("tenant-a");

        var prescriber = await client.GetAsync($"/api/prescribers/{b.ProviderId}");
        var epcs = await client.GetAsync($"/api/prescribers/{b.ProviderId}/epcs-status");
        var sso = await client.GetAsync($"/api/prescriptions/sso-url?providerId={b.ProviderId}");
        var notifications = await client.GetAsync($"/api/prescriptions/notifications/{b.ClinicianId}");
        var allergies = await client.GetFromJsonAsync<List<IdOnly>>($"/api/patients/{b.PatientId}/allergies");

        Assert.Equal(HttpStatusCode.NotFound, prescriber.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, epcs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sso.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notifications.StatusCode);
        Assert.Empty(allergies!);
    }

    [Fact]
    public async Task Own_tenant_routes_succeed_and_new_records_take_the_token_tenant()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var client = _factory.ClientFor("tenant-a");

        var get = await client.GetAsync($"/api/prescriptions/{a.PrescriptionId}");
        var created = await client.PostAsJsonAsync("/api/prescriptions", NewPrescription(a.PatientId, a.ProviderId));
        var createdId = (await created.Content.ReadFromJsonAsync<IdOnly>())!.Id;
        var allergy = await client.PostAsJsonAsync($"/api/patients/{a.PatientId}/allergies",
            new { AllergyName = "Penicillin", Severity = 2, Source = "Patient" });
        var allergies = await client.GetFromJsonAsync<List<IdOnly>>($"/api/patients/{a.PatientId}/allergies");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("tenant-a", (await _factory.PrescriptionAsync(createdId)).TenantId);
        Assert.Equal(HttpStatusCode.Created, allergy.StatusCode);
        Assert.Equal(2, allergies!.Count);
    }

    [Fact]
    public async Task Prescriptions_cannot_be_created_for_another_tenant_prescriber()
    {
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.ClientFor("tenant-a");

        var created = await client.PostAsJsonAsync("/api/prescriptions", NewPrescription(b.PatientId, b.ProviderId));

        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_patient_is_404_for_erx_checks_and_cannot_be_claimed()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.ClientFor("tenant-a");

        var interactions = await client.PostAsync($"/api/prescriptions/check-interactions?patientId={b.PatientId}&rxNormCode=723", null);
        var benefits = await client.PostAsJsonAsync("/api/prescriptions/check-benefits", new { PatientId = b.PatientId, DrugName = "Amoxicillin" });
        var history = await client.GetAsync($"/api/prescriptions/patient/{b.PatientId}/medication-history?includeInactive=true");
        var created = await client.PostAsJsonAsync("/api/prescriptions", NewPrescription(b.PatientId, a.ProviderId));
        var allergy = await client.PostAsJsonAsync($"/api/patients/{b.PatientId}/allergies", new { AllergyName = "Latex", Severity = 0 });

        Assert.Equal(HttpStatusCode.NotFound, interactions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, benefits.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, allergy.StatusCode);
    }

    [Fact]
    public async Task Erx_checks_work_for_own_and_brand_new_patients()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var client = _factory.ClientFor("tenant-a");
        var newPatient = Guid.NewGuid();

        var own = await client.GetAsync($"/api/prescriptions/patient/{a.PatientId}/medication-history?includeInactive=true");
        var fresh = await client.PostAsJsonAsync("/api/prescriptions/check-benefits", new { PatientId = newPatient, DrugName = "Amoxicillin" });
        var freshInteractions = await client.PostAsync($"/api/prescriptions/check-interactions?patientId={newPatient}&rxNormCode=723", null);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, freshInteractions.StatusCode);
    }

    [Fact]
    public async Task Controlled_substance_send_fails_closed_when_prescriber_is_not_in_tenant()
    {
        var b = await _factory.SeedTenantAsync("tenant-b");
        var controlledId = await _factory.SeedPrescriptionAsync("tenant-a", b.PatientId, b.ProviderId, schedule: 2);
        var client = _factory.ClientFor("tenant-a");

        var send = await client.PostAsJsonAsync($"/api/prescriptions/{controlledId}/send", new { PrescriptionId = controlledId });

        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
        Assert.Contains("EPCS prescriber not registered", await send.Content.ReadAsStringAsync());
        Assert.Equal("Draft", (await _factory.PrescriptionAsync(controlledId)).Status);
    }

    private static object NewPrescription(Guid patientId, Guid prescriberId) => new
    {
        PatientId = patientId, PrescriberId = prescriberId, DrugName = "Amoxicillin", Strength = "500 mg",
        DoseForm = "Capsule", Directions = "Take one capsule three times daily", Quantity = 21m,
        QuantityUnit = "Capsule", Refills = 0, DaysSupply = 7
    };

    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        return request;
    }

    private sealed record IdOnly(Guid Id);
}

public sealed class PrescriptionStartupTests
{
    [Fact]
    public void Missing_jwt_key_outside_development_fails_startup()
    {
        using var factory = PrescriptionSecurityFactory.For("Production", "");

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("Jwt:Key", error.Message);
    }
}

public sealed class PrescriptionSecurityFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-prescription-security-key-1234567890";
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-prescription-security-{Guid.NewGuid():N}.db");
    private readonly string _environment;
    private readonly string _jwtKey;

    public PrescriptionSecurityFactory() : this("Testing", JwtKey) { }

    private PrescriptionSecurityFactory(string environment, string jwtKey)
    {
        _environment = environment;
        _jwtKey = jwtKey;
    }

    public static PrescriptionSecurityFactory For(string environment, string jwtKey) => new(environment, jwtKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Jwt:Key", _jwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("ErxProvider", "Mock");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<PrescriptionDbContext>>();
            services.AddDbContext<PrescriptionDbContext>(options => options.UseSqlite($"Data Source={_database}"));
        });
    }

    // Mirrors the Portal's staff tokens: HMAC-SHA256 with the shared key and a tenant_id claim.
    public static string Token(string? tenantId, string key = JwtKey, string? role = "Staff")
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "staff-user") };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        if (tenantId is not null) claims.Add(new("tenant_id", tenantId));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer, Audience, claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
    }

    public HttpClient ClientFor(string tenantId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenantId));
        return client;
    }

    public sealed record TenantSeed(Guid PatientId, Guid ProviderId, string ClinicianId, Guid PrescriptionId);

    public async Task<TenantSeed> SeedTenantAsync(string tenantId)
    {
        var patientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var clinicianId = $"clinician-{Guid.NewGuid():N}";
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrescriptionDbContext>();
            db.Prescribers.Add(new Prescriber
            {
                ProviderId = providerId, TenantId = tenantId, FirstName = "Pat", LastName = "Provider",
                Npi = "1234567890", DoseSpotClinicianId = clinicianId, Status = "Active"
            });
            db.PatientAllergies.Add(new PatientAllergy
            {
                PatientId = patientId, TenantId = tenantId, AllergyName = "Latex", Severity = "Mild"
            });
            await db.SaveChangesAsync();
        }
        var prescriptionId = await SeedPrescriptionAsync(tenantId, patientId, providerId, schedule: 0);
        return new(patientId, providerId, clinicianId, prescriptionId);
    }

    public async Task<Guid> SeedPrescriptionAsync(string tenantId, Guid patientId, Guid prescriberId, int schedule)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrescriptionDbContext>();
        var prescription = new Prescription
        {
            PatientId = patientId, PrescriberId = prescriberId, TenantId = tenantId, DrugName = "Hydrocodone",
            Strength = "5 mg", DoseForm = "Tablet", Directions = "As directed", Quantity = 10,
            QuantityUnit = "Tablet", DaysSupply = 3, Schedule = schedule, Status = "Draft"
        };
        db.Prescriptions.Add(prescription);
        await db.SaveChangesAsync();
        return prescription.Id;
    }

    public async Task<Prescription> PrescriptionAsync(Guid id)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PrescriptionDbContext>().Prescriptions
            .Include(p => p.AuditTrail).AsNoTracking().SingleAsync(p => p.Id == id);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_database)) File.Delete(_database);
    }
}
