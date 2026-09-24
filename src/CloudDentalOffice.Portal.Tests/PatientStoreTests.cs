using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Patients live only in the Portal database. These tests fail if any Portal code path can
/// resolve a patient from anywhere else, including through configuration.
/// </summary>
public sealed class PatientStoreRegressionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void IPatientService_is_always_the_Portal_database_implementation(string? legacyFlag)
    {
        using var factory = new PortalFactory(configure: builder =>
        {
            if (legacyFlag is not null) builder.UseSetting("Microservices:Patient:Enabled", legacyFlag);
        });

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPatientService>();

        Assert.IsType<PatientServiceImpl>(service);
        Assert.Equal([typeof(PatientServiceImpl)], scope.ServiceProvider.GetServices<IPatientService>().Select(s => s.GetType()));
    }

    [Fact]
    public async Task Staff_created_patients_take_the_callers_tenant_and_updates_stay_in_it()
    {
        // A non-default tenant: the Portal DB's column default ("demo") must not mask a missing stamp.
        const string tenant = "practice-under-test";
        using var factory = new PortalFactory(configure: builder => builder.ConfigureTestServices(services =>
            services.AddScoped<CloudDentalOffice.Portal.Services.Tenancy.ITenantProvider>(_ => new FixedTenantProvider(tenant))));
        await using var scope = factory.Services.CreateAsyncScope();
        var patients = scope.ServiceProvider.GetRequiredService<IPatientService>();
        var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
        var foreign = new Patient
        {
            TenantId = "another-tenant", FirstName = "Other", LastName = "Tenant", DateOfBirth = DateTime.UtcNow.Date,
            Gender = "U", Status = "Active", CreatedDate = DateTime.UtcNow
        };
        db.Patients.Add(foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var created = await patients.CreatePatientAsync(new Patient
        {
            FirstName = "Front", LastName = "Desk", DateOfBirth = new DateTime(1990, 1, 1), Gender = "F"
        });

        Assert.Equal(tenant, created.TenantId);
        Assert.Contains(await patients.GetPatientsAsync(), p => p.PatientId == created.PatientId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => patients.UpdatePatientAsync(new Patient
        {
            PatientId = foreign.PatientId, TenantId = tenant, FirstName = "Hijacked", LastName = "Tenant",
            DateOfBirth = DateTime.UtcNow.Date, Gender = "U", Status = "Active"
        }));
    }

    [Fact]
    public void Only_the_Portal_database_implements_IPatientService()
    {
        var implementations = typeof(IPatientService).Assembly.GetTypes()
            .Where(t => typeof(IPatientService).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();

        Assert.Equal([typeof(PatientServiceImpl)], implementations);
        Assert.Contains(typeof(PatientServiceImpl).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(CloudDentalDbContext));
    }

    [Fact]
    public void Portal_source_has_no_remote_patient_store_calls_or_switch()
    {
        var root = RepositoryRoot();
        var portal = Path.Combine(root, "src", "CloudDentalOffice.Portal");
        // Remote patient routes (PatientService via the gateway) and the retired store switch.
        // The prescription service's /api/patients/{id}/allergies route is not a patient lookup.
        var forbidden = new Regex(@"/api/patients(?![^""]*/allergies)|/api/insurance-plans|Microservices:Patient|Microservices__Patient|PatientServiceHttpClient",
            RegexOptions.CultureInvariant);

        var offenders = Directory.EnumerateFiles(portal, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs") || path.EndsWith(".razor") || path.EndsWith(".json"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(x => forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.path)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Remote patient store references:\n" + string.Join('\n', offenders));
    }

    private sealed class FixedTenantProvider(string tenantId) : CloudDentalOffice.Portal.Services.Tenancy.ITenantProvider
    {
        public string TenantId => tenantId;
        public System.Security.Claims.ClaimsPrincipal? User => null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CloudDentalOffice.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class InternalPatientApiTests
{
    private const string ServiceKey = "internal-patient-api-test-key-1234567890";
    private const string Path = InternalPatientApi.MatchOrCreatePath + "?tenantId=tenant-a";

    [Fact]
    public async Task Match_or_create_is_not_found_on_the_public_port()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        var response = await client.SendAsync(Request(ServiceKey));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-internal-service-key-1234567890")]
    public async Task Match_or_create_rejects_a_missing_or_wrong_key(string? key)
    {
        using var factory = NewFactory();

        var response = await InternalClient(factory).SendAsync(Request(key));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Match_or_create_rejects_another_tenants_key_and_a_missing_tenant()
    {
        using var factory = NewFactory(builder =>
        {
            builder.UseSetting("InternalApi:Clients:1:TenantId", "tenant-b");
            builder.UseSetting("InternalApi:Clients:1:ApiKey", "tenant-b-internal-service-key-1234567890");
        });
        var client = InternalClient(factory);

        var otherTenantsKey = await client.SendAsync(Request("tenant-b-internal-service-key-1234567890"));
        var noTenant = await client.SendAsync(Request(ServiceKey, InternalPatientApi.MatchOrCreatePath));

        Assert.Equal(HttpStatusCode.Unauthorized, otherTenantsKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noTenant.StatusCode);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task Match_or_create_on_the_internal_port_with_a_valid_key_writes_to_the_Portal_database(string staffAuth)
    {
        using var factory = NewFactory(builder => builder.UseSetting("StaffAuth:Enabled", staffAuth));

        var response = await InternalClient(factory).SendAsync(Request(ServiceKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MatchOrCreateExternalPatientResult>();
        Assert.True(result!.Created);
        await using var scope = factory.Services.CreateAsyncScope();
        var patient = await scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>().Patients
            .IgnoreQueryFilters().SingleAsync(p => p.PatientId == result.PatientId);
        Assert.Equal("tenant-a", patient.TenantId);
        Assert.Equal("Zoe", patient.FirstName);
    }

    private static PortalFactory NewFactory(Action<IWebHostBuilder>? configure = null) => new(builder =>
    {
        builder.UseSetting("InternalApi:Clients:0:TenantId", "tenant-a");
        builder.UseSetting("InternalApi:Clients:0:ApiKey", ServiceKey);
        configure?.Invoke(builder);
    });

    private static HttpClient InternalClient(PortalFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"http://localhost:{InternalPatientApi.DefaultPort}")
    });

    private static HttpRequestMessage Request(string? key, string path = Path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new MatchOrCreateExternalPatientRequest
            {
                FirstName = "Zoe", LastName = "Zocdoc", DateOfBirth = new DateOnly(1991, 4, 5), Email = "zoe@example.test"
            })
        };
        if (key is not null) request.Headers.Add(InternalServiceKey.Header, key);
        return request;
    }
}

/// <summary>Matching rules ported from PatientService's match-or-create.</summary>
public sealed class ExternalPatientMatcherTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly ExternalPatientMatcher _matcher;

    public ExternalPatientMatcherTests()
    {
        _connection.Open();
        _db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _matcher = new ExternalPatientMatcher(_db);
    }

    [Fact]
    public async Task Known_developer_patient_id_in_the_tenant_wins()
    {
        var known = await AddAsync("tenant-a", "Ann", "Known", new DateTime(1980, 1, 1));

        var result = await _matcher.MatchOrCreateAsync("tenant-a", Request("Different", "Name", new DateOnly(2000, 2, 2), developerId: known.PatientId));

        Assert.Equal(new MatchOrCreateExternalPatientResult(known.PatientId, false), result);
    }

    [Fact]
    public async Task Known_id_from_another_tenant_or_archived_is_ignored()
    {
        var otherTenant = await AddAsync("tenant-b", "Ann", "Other", new DateTime(1980, 1, 1));
        var archived = await AddAsync("tenant-a", "Arch", "Ived", new DateTime(1980, 1, 1), status: "Archived");

        var first = await _matcher.MatchOrCreateAsync("tenant-a", Request("New", "One", new DateOnly(1990, 1, 1), developerId: otherTenant.PatientId));
        var second = await _matcher.MatchOrCreateAsync("tenant-a", Request("Arch", "Ived", new DateOnly(1980, 1, 1), developerId: archived.PatientId));

        Assert.True(first!.Created);
        Assert.True(second!.Created);
        Assert.NotEqual(archived.PatientId, second.PatientId);
    }

    [Fact]
    public async Task Name_and_date_of_birth_match_is_trimmed_and_case_insensitive()
    {
        var existing = await AddAsync("tenant-a", "Maria", "Lopez", new DateTime(1975, 6, 7));

        var result = await _matcher.MatchOrCreateAsync("tenant-a", Request("  maria ", "LOPEZ", new DateOnly(1975, 6, 7)));

        Assert.Equal(new MatchOrCreateExternalPatientResult(existing.PatientId, false), result);
    }

    [Fact]
    public async Task Duplicate_names_are_narrowed_by_email_then_phone_and_ambiguity_returns_null()
    {
        var byEmail = await AddAsync("tenant-a", "Sam", "Twin", new DateTime(1990, 3, 3), email: "one@example.test", phone: "111");
        var byPhone = await AddAsync("tenant-a", "Sam", "Twin", new DateTime(1990, 3, 3), email: "two@example.test", phone: "222");

        var emailMatch = await _matcher.MatchOrCreateAsync("tenant-a", Request("Sam", "Twin", new DateOnly(1990, 3, 3), email: "ONE@example.test"));
        var phoneMatch = await _matcher.MatchOrCreateAsync("tenant-a", Request("Sam", "Twin", new DateOnly(1990, 3, 3), phone: "222"));
        var ambiguous = await _matcher.MatchOrCreateAsync("tenant-a", Request("Sam", "Twin", new DateOnly(1990, 3, 3)));

        Assert.Equal(byEmail.PatientId, emailMatch!.PatientId);
        Assert.Equal(byPhone.PatientId, phoneMatch!.PatientId);
        Assert.Null(ambiguous);
    }

    [Fact]
    public async Task Unmatched_patient_is_created_in_the_callers_tenant()
    {
        await AddAsync("tenant-b", "Noah", "Elsewhere", new DateTime(1985, 8, 9));

        var result = await _matcher.MatchOrCreateAsync("tenant-a", Request("Noah", "Elsewhere", new DateOnly(1985, 8, 9), email: "noah@example.test"));

        Assert.True(result!.Created);
        var created = await _db.Patients.IgnoreQueryFilters().SingleAsync(p => p.PatientId == result.PatientId);
        Assert.Equal(("tenant-a", "Active", "noah@example.test"), (created.TenantId, created.Status, created.Email));
        Assert.Equal(new DateTime(1985, 8, 9), created.DateOfBirth.Date);
    }

    private async Task<Patient> AddAsync(string tenantId, string first, string last, DateTime dob,
        string status = "Active", string? email = null, string? phone = null)
    {
        var patient = new Patient
        {
            TenantId = tenantId, FirstName = first, LastName = last, DateOfBirth = DateTime.SpecifyKind(dob, DateTimeKind.Utc),
            Gender = "U", Status = status, Email = email, PrimaryPhone = phone, CreatedDate = DateTime.UtcNow
        };
        _db.Patients.Add(patient);
        await _db.SaveChangesAsync();
        return patient;
    }

    private static MatchOrCreateExternalPatientRequest Request(string first, string last, DateOnly dob,
        int? developerId = null, string? email = null, string? phone = null) => new()
    {
        FirstName = first, LastName = last, DateOfBirth = dob, DeveloperPatientId = developerId?.ToString(),
        Email = email, Phone = phone
    };

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

internal sealed class PortalFactory(Action<IWebHostBuilder>? configure = null) : WebApplicationFactory<Program>
{
    private readonly string _database = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cdo-portal-store-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Jwt:Key", "portal-patient-store-test-key-1234567890");
        builder.UseSetting("AzureAd:Enabled", "false");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_database}");
        configure?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_database)) File.Delete(_database);
    }
}
