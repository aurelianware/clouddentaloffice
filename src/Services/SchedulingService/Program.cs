using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;

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

// Consume public booking-request events from Service Bus and turn them into
// (unconfirmed) appointments. Runs only when ServiceBus is configured; the
// consumer self-guards otherwise. Public booking traffic never reaches this
// service directly — the internet-facing IntakeService publishes the events.
var serviceBusOptions = new ServiceBusOptions();
builder.Configuration.GetSection(ServiceBusOptions.SectionName).Bind(serviceBusOptions);
builder.Services.AddSingleton(serviceBusOptions);
builder.Services.AddHostedService<BookingRequestConsumer>();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
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
