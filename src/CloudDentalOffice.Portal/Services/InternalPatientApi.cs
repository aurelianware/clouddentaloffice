using System.Security.Cryptography;
using System.Text;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Service-to-service patient resolution for SchedulingService (Zocdoc webhooks). It is served
/// only on a separate internal Kestrel port that is never published on the public ingress, and
/// it also requires a tenant-bound service key.
/// </summary>
public static class InternalPatientApi
{
    public const string MatchOrCreatePath = "/api/internal/patients/match-or-create";
    public const string PortSetting = "InternalApi:Port";
    public const int DefaultPort = 5091;

    public static int Port(IConfiguration configuration) => configuration.GetValue(PortSetting, DefaultPort);

    /// <summary>Adds the internal port to the addresses Kestrel already listens on.</summary>
    public static void AddInternalPatientApi(this WebApplicationBuilder builder)
    {
        var publicUrls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
        builder.WebHost.UseUrls(
            $"{(string.IsNullOrWhiteSpace(publicUrls) ? "http://localhost:5000" : publicUrls)};http://+:{Port(builder.Configuration)}");
        builder.Services.AddScoped<ExternalPatientMatcher>();
    }

    public static void MapInternalPatientApi(this WebApplication app)
    {
        var port = Port(app.Configuration);
        app.MapPost(MatchOrCreatePath, MatchOrCreate)
            .RequireHost($"*:{port}")
            // RequireHost matches the Host header; also require the request to have arrived on
            // the internal socket. (The in-memory test server has no socket and reports port 0.)
            .AddEndpointFilter(async (context, next) =>
            {
                var localPort = context.HttpContext.Connection.LocalPort;
                return localPort != 0 && localPort != port ? Results.NotFound() : await next(context);
            })
            .WithMetadata(new InternalServiceEndpointMetadata())
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Any other host (the public ingress) gets a plain 404 rather than falling through to the
        // Blazor fallback page. The host-bound endpoint above wins on the internal port (lower order).
        app.MapMethods(MatchOrCreatePath, [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch],
                () => Results.NotFound())
            .WithOrder(1)
            .ExcludeFromDescription();
    }

    /// <summary>True when the request was routed to an internal service endpoint.</summary>
    public static bool IsInternalServiceRequest(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<InternalServiceEndpointMetadata>() is not null;

    private static async Task<IResult> MatchOrCreate(
        MatchOrCreateExternalPatientRequest request, string? tenantId, HttpContext http,
        IConfiguration configuration, ExternalPatientMatcher matcher, CancellationToken cancellationToken)
    {
        if (!InternalServiceKey.IsAuthorized(http, configuration, tenantId)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName) ||
            request.DateOfBirth == default)
            return Results.BadRequest();

        var result = await matcher.MatchOrCreateAsync(tenantId!, request, cancellationToken);
        return result is null ? Results.Conflict() : Results.Ok(result);
    }

    private sealed class InternalServiceEndpointMetadata;
}

/// <summary>
/// Tenant-bound service key (X-CDO-Service-Key), compared in constant time. Configured as
/// InternalApi:Clients:{i}:TenantId / ApiKey. Fails closed when nothing is configured.
/// </summary>
public static class InternalServiceKey
{
    public const string Header = "X-CDO-Service-Key";

    public static bool IsAuthorized(HttpContext http, IConfiguration configuration, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        var provided = http.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(provided)) return false;
        var client = configuration.GetSection("InternalApi:Clients").GetChildren()
            .FirstOrDefault(x => string.Equals(x["TenantId"], tenantId, StringComparison.Ordinal));
        var expected = client?["ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

/// <summary>
/// Matches an externally booked patient (Zocdoc) to a Portal patient, or creates one.
/// Ported unchanged from PatientService's match-or-create: a known developer patient id wins,
/// then first name + last name + date of birth, narrowed by email and then phone; an
/// ambiguous match returns null (409) instead of guessing.
/// </summary>
public sealed class ExternalPatientMatcher(CloudDentalDbContext db)
{
    public async Task<MatchOrCreateExternalPatientResult?> MatchOrCreateAsync(
        string tenantId, MatchOrCreateExternalPatientRequest request, CancellationToken cancellationToken = default)
    {
        // Service calls carry no staff user, so the tenant filter is replaced by the key-bound tenant.
        var patients = db.Patients.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.Status != "Archived");

        if (int.TryParse(request.DeveloperPatientId, out var knownId))
        {
            var known = await patients.SingleOrDefaultAsync(x => x.PatientId == knownId, cancellationToken);
            if (known is not null) return new MatchOrCreateExternalPatientResult(known.PatientId, false);
        }

        var dob = request.DateOfBirth.ToDateTime(TimeOnly.MinValue);
        var firstName = request.FirstName.Trim().ToLower();
        var lastName = request.LastName.Trim().ToLower();
        var candidates = await patients.Where(x =>
                x.FirstName.ToLower() == firstName && x.LastName.ToLower() == lastName && x.DateOfBirth.Date == dob.Date)
            .ToListAsync(cancellationToken);
        if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(request.Email))
            candidates = candidates.Where(x => string.Equals(x.Email, request.Email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(request.Phone))
            candidates = candidates.Where(x => x.PrimaryPhone == request.Phone).ToList();
        if (candidates.Count == 1)
            return new MatchOrCreateExternalPatientResult(candidates[0].PatientId, false);
        if (candidates.Count > 1) return null;

        var patient = new Patient
        {
            TenantId = tenantId, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(),
            DateOfBirth = DateTime.SpecifyKind(dob, DateTimeKind.Utc), Gender = request.Gender ?? "U",
            Email = request.Email, PrimaryPhone = request.Phone, Status = "Active", CreatedDate = DateTime.UtcNow
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync(cancellationToken);
        return new MatchOrCreateExternalPatientResult(patient.PatientId, true);
    }
}
