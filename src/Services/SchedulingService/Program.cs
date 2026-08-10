using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SchedulingDbContext>(options =>
{
    var provider = builder.Configuration.GetValue("DatabaseProvider", "Sqlite");
    switch (provider)
    {
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("SchedulingDb"));
            break;
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("SchedulingDb"));
            break;
        default:
            options.UseSqlite(builder.Configuration.GetConnectionString("SchedulingDb") ?? "Data Source=scheduling.db");
            break;
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Scheduling Service", Version = "v1" }));
builder.Services.AddHealthChecks();

// Rate limiting for the internet-facing public booking endpoint. Partitions by
// the caller's forwarded client IP (the ApiGateway/edge sets X-Forwarded-For)
// so one visitor cannot flood the intake. Other endpoints are unaffected.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-booking", httpContext =>
    {
        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        var clientKey = !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRateLimiter();
app.MapHealthChecks("/health");

// These read endpoints return full appointment records, including the free-text
// Notes that public booking intakes use to carry patient contact details. They
// are anonymous by default (trusted internal callers such as the Portal reach
// them through a private gateway). If the same gateway is ever exposed to the
// internet, set PublicBooking:RequireApiKeyForReads=true so reads require the
// API key too — otherwise contact details would be publicly readable.
app.MapGet("/api/appointments", async (SchedulingDbContext db, IConfiguration config, HttpContext http, DateTime? from, DateTime? to) =>
{
    if (!PublicBookingAuth.ReadsAllowed(config, http)) return Results.Unauthorized();
    var query = db.Appointments.AsQueryable();
    if (from.HasValue) query = query.Where(a => a.StartTime >= from.Value);
    if (to.HasValue) query = query.Where(a => a.StartTime <= to.Value);
    return Results.Ok(await query.OrderBy(a => a.StartTime).Take(100).ToListAsync());
}).WithTags("Appointments");

app.MapGet("/api/appointments/{id:guid}", async (Guid id, SchedulingDbContext db, IConfiguration config, HttpContext http) =>
{
    if (!PublicBookingAuth.ReadsAllowed(config, http)) return Results.Unauthorized();
    var apt = await db.Appointments.FindAsync(id);
    return apt is not null ? Results.Ok(apt) : Results.NotFound();
}).WithTags("Appointments");

app.MapPost("/api/appointments", async (CreateAppointmentRequest request, SchedulingDbContext db) =>
{
    var apt = new Appointment
    {
        Id = Guid.NewGuid(),
        PatientId = request.PatientId,
        ProviderId = request.ProviderId,
        StartTime = request.StartTime,
        EndTime = request.EndTime,
        Status = AppointmentStatus.Scheduled,
        ProcedureCodes = request.ProcedureCodes,
        Notes = request.Notes,
        Operatory = request.Operatory,
        LocationId = request.LocationId,
        CreatedAt = DateTime.UtcNow
    };
    db.Appointments.Add(apt);
    await db.SaveChangesAsync();
    return Results.Created($"/api/appointments/{apt.Id}", apt);
}).WithTags("Appointments");

// Public, internet-facing booking intake for practice websites.
//
// Unlike POST /api/appointments (an internal/admin endpoint that trusts the
// caller-supplied Patient/Provider/Location IDs), this endpoint:
//   - is disabled unless PublicBooking:Enabled is true,
//   - requires an API key (Authorization: Bearer <key> or X-Api-Key: <key>),
//   - resolves Provider/Location/Patient server-side from configuration and
//     ignores any identifiers a caller might try to supply,
//   - records the appointment as AppointmentStatus.Requested (unconfirmed), and
//   - is rate limited per client IP.
//
// It must ONLY be exposed publicly through the ApiGateway; the raw service
// should not be reachable from the internet.
app.MapPost("/api/public/booking-requests", async (
    PublicBookingRequest request,
    SchedulingDbContext db,
    IConfiguration config,
    ILoggerFactory loggerFactory,
    HttpContext http) =>
{
    var section = config.GetSection("PublicBooking");
    if (!section.GetValue("Enabled", false))
        return Results.NotFound();

    var apiKey = section.GetValue<string>("ApiKey");
    if (string.IsNullOrWhiteSpace(apiKey) || !PublicBookingAuth.IsAuthorized(http, apiKey))
        return Results.Unauthorized();

    // Fail fast on server misconfiguration rather than writing invalid
    // appointments (FK violations, or EndTime not after StartTime).
    var providerId = section.GetValue<Guid>("ProviderId");
    var patientId = section.GetValue<Guid>("PatientId");
    var locationId = section.GetValue<Guid>("LocationId");
    var defaultDurationMinutes = section.GetValue("DefaultDurationMinutes", 60);
    if (providerId == Guid.Empty || patientId == Guid.Empty || defaultDurationMinutes <= 0)
    {
        loggerFactory.CreateLogger("PublicBooking").LogError(
            "PublicBooking is enabled but misconfigured (ProviderId/PatientId/DefaultDurationMinutes).");
        return Results.Problem(
            title: "Online booking is temporarily unavailable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name))
        errors["name"] = new[] { "Name is required." };
    if (string.IsNullOrWhiteSpace(request.Phone))
        errors["phone"] = new[] { "Phone is required." };

    // Require an unambiguous instant: UTC ("Z") or an explicit offset. A value
    // with no timezone (DateTimeKind.Unspecified) is rejected rather than
    // silently assumed to be UTC, which could shift the requested time.
    if (request.PreferredStart == default)
        errors["preferredStart"] = new[] { "A preferred start time is required." };
    else if (request.PreferredStart.Kind == DateTimeKind.Unspecified)
        errors["preferredStart"] = new[] { "preferredStart must include a timezone (UTC 'Z' or an offset)." };

    var preferredStartUtc = request.PreferredStart.ToUniversalTime();
    if (!errors.ContainsKey("preferredStart") && preferredStartUtc <= DateTime.UtcNow)
        errors["preferredStart"] = new[] { "preferredStart must be in the future." };

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var durationMinutes = request.DurationMinutes is int d && d > 0 ? d : defaultDurationMinutes;

    var notes = string.Join("\n", new[]
    {
        "WEB BOOKING REQUEST — confirm with patient before finalizing.",
        $"Name: {request.Name}",
        $"Phone: {request.Phone}",
        string.IsNullOrWhiteSpace(request.Email) ? null : $"Email: {request.Email}",
        string.IsNullOrWhiteSpace(request.Reason) ? null : $"Reason: {request.Reason}",
        string.IsNullOrWhiteSpace(request.Message) ? null : $"Message: {request.Message}"
    }.Where(line => line is not null));

    var apt = new Appointment
    {
        Id = Guid.NewGuid(),
        PatientId = patientId,
        ProviderId = providerId,
        StartTime = preferredStartUtc,
        EndTime = preferredStartUtc.AddMinutes(durationMinutes),
        Status = AppointmentStatus.Requested,
        ProcedureCodes = null,
        Notes = notes,
        Operatory = null,
        LocationId = locationId == Guid.Empty ? null : locationId,
        CreatedAt = DateTime.UtcNow
    };
    db.Appointments.Add(apt);
    await db.SaveChangesAsync();

    // Return a minimal confirmation — do not echo internal identifiers.
    return Results.Created($"/api/appointments/{apt.Id}", new
    {
        id = apt.Id,
        status = apt.Status.ToString(),
        startTime = apt.StartTime,
        endTime = apt.EndTime
    });
})
.RequireRateLimiting("public-booking")
.WithTags("PublicBooking");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();

public class Appointment
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? ProcedureCodes { get; set; }
    public string? Notes { get; set; }
    public string? Operatory { get; set; }
    public Guid? LocationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();
}

internal static class PublicBookingAuth
{
    /// <summary>
    /// Validates the request's API key against the configured value using a
    /// constant-time comparison. Accepts either an "Authorization: Bearer &lt;key&gt;"
    /// header or an "X-Api-Key: &lt;key&gt;" header.
    /// </summary>
    public static bool IsAuthorized(HttpContext http, string expectedKey)
    {
        string? provided = null;

        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            provided = auth["Bearer ".Length..].Trim();

        if (string.IsNullOrEmpty(provided))
        {
            var apiKeyHeader = http.Request.Headers["X-Api-Key"].ToString();
            if (!string.IsNullOrEmpty(apiKeyHeader))
                provided = apiKeyHeader.Trim();
        }

        if (string.IsNullOrEmpty(provided))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    /// <summary>
    /// Whether an appointment read should be allowed. Reads are open by default
    /// (trusted internal callers via a private gateway). When
    /// PublicBooking:RequireApiKeyForReads is true — e.g. the gateway is exposed
    /// publicly — reads require the same API key as the booking endpoint.
    /// </summary>
    public static bool ReadsAllowed(IConfiguration config, HttpContext http)
    {
        var section = config.GetSection("PublicBooking");
        if (!section.GetValue("RequireApiKeyForReads", false))
            return true;

        var apiKey = section.GetValue<string>("ApiKey");
        return !string.IsNullOrWhiteSpace(apiKey) && IsAuthorized(http, apiKey);
    }
}
