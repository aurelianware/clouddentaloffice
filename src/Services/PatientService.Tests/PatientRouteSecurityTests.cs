using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Contracts.Patients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class PatientRouteSecurityTests : IClassFixture<PatientSecurityFactory>
{
    private readonly PatientSecurityFactory _factory;

    public PatientRouteSecurityTests(PatientSecurityFactory factory) => _factory = factory;

    public static TheoryData<string, string> PublicRoutes() => new()
    {
        { "GET", "/api/patients" },
        { "POST", "/api/patients" },
        { "GET", "/api/patients/search?q=a" },
        { "GET", "/api/patients/1" },
        { "PUT", "/api/patients/1" },
        { "DELETE", "/api/patients/1" },
        { "POST", "/api/patients/1/insurances" },
        { "GET", "/api/insurance-plans" },
        { "POST", "/api/insurance-plans" },
        { "GET", "/api/insurance-plans/1" }
    };

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task Public_routes_reject_requests_without_a_token(string method, string route)
    {
        var response = await _factory.CreateClient().SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task Public_routes_reject_tokens_signed_with_another_key(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            PatientSecurityFactory.Token("tenant-a", "a-different-signing-key-with-at-least-32-bytes"));

        var response = await client.SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task Public_routes_forbid_tokens_without_tenant_claim(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", PatientSecurityFactory.Token(tenantId: null));

        var response = await client.SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task Public_routes_forbid_patient_portal_tokens(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", PatientSecurityFactory.Token("tenant-a", role: "Patient"));

        var response = await client.SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_without_a_role_are_forbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", PatientSecurityFactory.Token("tenant-a", role: null));

        var response = await client.GetAsync("/api/patients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Responses_exclude_insurance_rows_linked_to_another_tenant_plan()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        await _factory.AddInsuranceAsync("tenant-a", seed.PatientA, seed.PlanA);
        await _factory.AddInsuranceAsync("tenant-a", seed.PatientA, seed.PlanB); // legacy cross-tenant link
        var client = _factory.ClientFor("tenant-a");

        var list = (await client.GetFromJsonAsync<List<PatientDto>>("/api/patients"))!.Single(p => p.PatientId == seed.PatientA);
        var search = (await client.GetFromJsonAsync<List<PatientDto>>($"/api/patients/search?q={seed.Surname}"))!.Single();
        var get = (await client.GetFromJsonAsync<PatientDto>($"/api/patients/{seed.PatientA}"))!;
        var put = await (await client.PutAsJsonAsync($"/api/patients/{seed.PatientA}", new UpdatePatientRequest { City = "Austin" }))
            .Content.ReadFromJsonAsync<PatientDto>();

        foreach (var patient in new[] { list, search, get, put! })
            Assert.Equal([seed.PlanA], patient.Insurances.Select(i => i.InsurancePlanId));
    }

    [Fact]
    public async Task Lists_and_search_return_only_the_token_tenant()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        var client = _factory.ClientFor("tenant-a");

        var patients = await client.GetFromJsonAsync<List<PatientDto>>("/api/patients");
        var search = await client.GetFromJsonAsync<List<PatientDto>>($"/api/patients/search?q={seed.Surname}");
        var plans = await client.GetFromJsonAsync<List<InsurancePlanDto>>("/api/insurance-plans");

        Assert.Contains(patients!, p => p.PatientId == seed.PatientA);
        Assert.DoesNotContain(patients!, p => p.PatientId == seed.PatientB);
        Assert.Equal([seed.PatientA], search!.Select(p => p.PatientId));
        Assert.Contains(plans!, p => p.InsurancePlanId == seed.PlanA);
        Assert.DoesNotContain(plans!, p => p.InsurancePlanId == seed.PlanB);
    }

    [Fact]
    public async Task Tenant_query_parameter_is_ignored()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        var client = _factory.ClientFor("tenant-a");

        var patients = await client.GetFromJsonAsync<List<PatientDto>>("/api/patients?tenantId=tenant-b");
        var created = await client.PostAsJsonAsync("/api/patients?tenantId=tenant-b", NewPatient("Created"));
        var createdId = (await created.Content.ReadFromJsonAsync<PatientDto>())!.PatientId;

        Assert.DoesNotContain(patients!, p => p.PatientId == seed.PatientB);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("tenant-a", await _factory.PatientTenantAsync(createdId));
    }

    [Fact]
    public async Task Cross_tenant_patient_reads_and_writes_return_404_and_leave_row_unchanged()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        var client = _factory.ClientFor("tenant-a");
        var before = await _factory.PatientAsync(seed.PatientB);

        var get = await client.GetAsync($"/api/patients/{seed.PatientB}");
        var put = await client.PutAsJsonAsync($"/api/patients/{seed.PatientB}",
            new UpdatePatientRequest { FirstName = "Hijacked", Status = "Inactive" });
        var delete = await client.DeleteAsync($"/api/patients/{seed.PatientB}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var after = await _factory.PatientAsync(seed.PatientB);
        Assert.Equal(before.FirstName, after.FirstName);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    [Fact]
    public async Task Own_tenant_patient_reads_and_writes_succeed()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        var client = _factory.ClientFor("tenant-a");

        var get = await client.GetAsync($"/api/patients/{seed.PatientA}");
        var put = await client.PutAsJsonAsync($"/api/patients/{seed.PatientA}", new UpdatePatientRequest { FirstName = "Renamed" });
        var delete = await client.DeleteAsync($"/api/patients/{seed.PatientA}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal("Archived", (await _factory.PatientAsync(seed.PatientA)).Status);
    }

    [Fact]
    public async Task Cross_tenant_insurance_plan_and_nested_insurance_routes_return_404()
    {
        var seed = await _factory.SeedTwoTenantsAsync();
        var client = _factory.ClientFor("tenant-a");

        var plan = await client.GetAsync($"/api/insurance-plans/{seed.PlanB}");
        var onOtherPatient = await client.PostAsJsonAsync($"/api/patients/{seed.PatientB}/insurances", NewInsurance(seed.PlanA));
        var withOtherPlan = await client.PostAsJsonAsync($"/api/patients/{seed.PatientA}/insurances", NewInsurance(seed.PlanB));
        var own = await client.PostAsJsonAsync($"/api/patients/{seed.PatientA}/insurances", NewInsurance(seed.PlanA));

        Assert.Equal(HttpStatusCode.NotFound, plan.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, onOtherPatient.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withOtherPlan.StatusCode);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        Assert.Equal(0, await _factory.InsuranceCountAsync(seed.PatientB));
    }

    [Fact]
    public async Task Match_or_create_accepts_valid_service_key_and_rejects_bad_key()
    {
        var client = _factory.CreateClient();
        var request = new MatchOrCreateExternalPatientRequest
        {
            FirstName = "Internal", LastName = "Match", DateOfBirth = new DateOnly(1990, 1, 2)
        };

        using var valid = new HttpRequestMessage(HttpMethod.Post, "/api/internal/patients/match-or-create?tenantId=tenant-a")
        { Content = JsonContent.Create(request) };
        valid.Headers.Add("X-CDO-Service-Key", PatientSecurityFactory.ServiceKey);
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/internal/patients/match-or-create?tenantId=tenant-a")
        { Content = JsonContent.Create(request) };
        invalid.Headers.Add("X-CDO-Service-Key", "wrong-service-key-with-at-least-32-bytes!");

        var accepted = await client.SendAsync(valid);
        var rejected = await client.SendAsync(invalid);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<MatchOrCreateExternalPatientResult>())!.PatientId > 0);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        return request;
    }

    private static CreatePatientRequest NewPatient(string firstName) => new()
    {
        FirstName = firstName, LastName = "Patient", DateOfBirth = new DateTime(1990, 1, 1), Gender = "U"
    };

    private static CreatePatientInsuranceRequest NewInsurance(int planId) => new()
    {
        InsurancePlanId = planId, MemberId = "M-1", SequenceNumber = 1, EffectiveDate = new DateTime(2024, 1, 1)
    };
}

public sealed class PatientStartupTests
{
    [Fact]
    public void Missing_jwt_key_outside_development_fails_startup()
    {
        using var factory = PatientSecurityFactory.For("Production", "");

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("Jwt:Key", error.Message);
    }

    [Fact]
    public void Short_jwt_key_outside_development_fails_startup()
    {
        using var factory = PatientSecurityFactory.For("Production", "too-short");

        Assert.Throws<InvalidOperationException>(() => factory.Services);
    }

    [Fact]
    public async Task Missing_jwt_key_in_development_starts_and_rejects_every_token()
    {
        using var factory = PatientSecurityFactory.For("Development", "");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", PatientSecurityFactory.Token("tenant-a"));

        var response = await client.GetAsync("/api/patients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class PatientSecurityFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-patient-security-key-1234567890";
    public const string ServiceKey = "test-only-patient-service-key-1234567890";
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-patient-security-{Guid.NewGuid():N}.db");
    private readonly string _environment;
    private readonly string _jwtKey;

    public PatientSecurityFactory() : this("Testing", JwtKey) { }

    private PatientSecurityFactory(string environment, string jwtKey)
    {
        _environment = environment;
        _jwtKey = jwtKey;
    }

    public static PatientSecurityFactory For(string environment, string jwtKey) => new(environment, jwtKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("DatabaseProvider", "Sqlite");
        builder.UseSetting("ConnectionStrings:PatientDb", $"Data Source={_database}");
        builder.UseSetting("Jwt:Key", _jwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("InternalApi:Clients:0:TenantId", "tenant-a");
        builder.UseSetting("InternalApi:Clients:0:ApiKey", ServiceKey);
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

    public sealed record TwoTenantSeed(int PatientA, int PatientB, int PlanA, int PlanB, string Surname);

    public async Task<TwoTenantSeed> SeedTwoTenantsAsync()
    {
        var surname = $"Seed{Guid.NewGuid():N}"[..20];
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
        var patientA = Patient("tenant-a", surname);
        var patientB = Patient("tenant-b", surname);
        var planA = Plan("tenant-a");
        var planB = Plan("tenant-b");
        db.AddRange(patientA, patientB, planA, planB);
        await db.SaveChangesAsync();
        return new(patientA.PatientId, patientB.PatientId, planA.InsurancePlanId, planB.InsurancePlanId, surname);
    }

    public async Task AddInsuranceAsync(string tenantId, int patientId, int planId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
        db.PatientInsurances.Add(new PatientInsuranceEntity
        {
            TenantId = tenantId, PatientId = patientId, InsurancePlanId = planId, MemberId = $"M-{planId}",
            SequenceNumber = 1, EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    public async Task<PatientEntity> PatientAsync(int id)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PatientDbContext>().Patients.AsNoTracking().SingleAsync(p => p.PatientId == id);
    }

    public async Task<string> PatientTenantAsync(int id) => (await PatientAsync(id)).TenantId;

    public async Task<int> InsuranceCountAsync(int patientId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PatientDbContext>().PatientInsurances.CountAsync(i => i.PatientId == patientId);
    }

    private static PatientEntity Patient(string tenantId, string surname) => new()
    {
        TenantId = tenantId, FirstName = tenantId, LastName = surname, DateOfBirth = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc),
        Status = "Active", ModifiedDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static InsurancePlanEntity Plan(string tenantId) => new()
    {
        TenantId = tenantId, PayerId = "P1", PayerName = $"{tenantId} payer"
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_database)) File.Delete(_database);
    }
}
