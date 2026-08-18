using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddSchedulingIntegrations();

// When configured, administrative scheduling-integration routes accept the
// same bearer tokens as the portal. If Jwt:Key is absent, a per-process random
// key deliberately keeps every admin request unauthorized (fail closed).
var configuredJwtKey = builder.Configuration["Jwt:Key"];
var signingKey = string.IsNullOrWhiteSpace(configuredJwtKey)
    ? RandomNumberGenerator.GetBytes(32)
    : Encoding.UTF8.GetBytes(configuredJwtKey);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(signingKey),
        ValidateIssuer = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Issuer"]),
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Audience"]),
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});
builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapSchedulingIntegrationAdminApi();

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
        TenantId = request.TenantId,
        PatientId = request.PatientId,
        ProviderId = request.ProviderId,
        StartTime = SchedulingTime.NormalizeUtc(request.StartTime),
        EndTime = SchedulingTime.NormalizeUtc(request.EndTime),
        Status = AppointmentStatus.Scheduled,
        ProcedureCodes = request.ProcedureCodes,
        Notes = request.Notes,
        Operatory = request.Operatory,
        LocationId = request.LocationId,
        AppointmentTypeId = request.AppointmentTypeId,
        CreatedAt = DateTime.UtcNow
    };
    db.Appointments.Add(apt);
    await db.SaveChangesAsync();
    return Results.Created($"/api/appointments/{apt.Id}", apt);
}).WithTags("Appointments");

app.MapGet("/api/booking-requests", async (SchedulingDbContext db, IConfiguration config, HttpContext http, string tenantId, string? status) =>
{
    if (!PublicBookingAuth.ReadsAllowed(config, http)) return Results.Unauthorized();
    var query = db.BookingRequests.Where(r => r.TenantId == tenantId);
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BookingRequestStatus>(status, true, out var parsed))
        query = query.Where(r => r.Status == parsed);
    else if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"Unknown booking request status '{status}'."] });
    else if (string.IsNullOrWhiteSpace(status))
        query = query.Where(r => r.Status != BookingRequestStatus.Approved &&
                                 r.Status != BookingRequestStatus.Rejected &&
                                 r.Status != BookingRequestStatus.Cancelled);
    return Results.Ok(await query.OrderBy(r => r.CreatedAt).Select(r => r.ToDto()).ToListAsync());
}).WithTags("BookingRequests");

app.MapGet("/api/booking-requests/{id:guid}", async (Guid id, string tenantId, SchedulingDbContext db, IConfiguration config, HttpContext http) =>
{
    if (!PublicBookingAuth.ReadsAllowed(config, http)) return Results.Unauthorized();
    var request = await db.BookingRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId);
    return request is null ? Results.NotFound() : Results.Ok(request.ToDto());
}).WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/match-patient", async (
    Guid id, string tenantId, MatchBookingPatientRequest match, SchedulingDbContext db) =>
{
    try { return Results.Ok((await new BookingRequestWorkflow(db).MatchPatientAsync(id, tenantId, match)).ToDto()); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["patientId"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/status", async (
    Guid id, string tenantId, ChangeBookingRequestStatusRequest change, SchedulingDbContext db) =>
{
    try { return Results.Ok((await new BookingRequestWorkflow(db).ChangeStatusAsync(id, tenantId, change)).ToDto()); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/approve", async (
    Guid id, string tenantId, ApproveBookingRequest approval, SchedulingDbContext db) =>
{
    try
    {
        var result = await new BookingRequestWorkflow(db).ApproveAsync(id, tenantId, approval);
        return Results.Ok(new { bookingRequest = result.Request.ToDto(), appointmentId = result.Appointment.Id, created = result.Created });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).WithTags("BookingRequests");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
    await db.Database.EnsureCreatedAsync();
    await BookingRequestSchema.EnsureAsync(db);
    await SchedulingIntegrationSchema.EnsureAsync(db);
    await SchedulingAvailabilitySchema.EnsureAsync(db);
}

app.Run();

public class Appointment
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "default";
    public int PatientId { get; set; }
    public int ProviderId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? ProcedureCodes { get; set; }
    public string? Notes { get; set; }
    public string? Operatory { get; set; }
    public Guid? LocationId { get; set; }
    public string? AppointmentTypeId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<BookingRequest> BookingRequests => Set<BookingRequest>();
    public DbSet<SchedulingIntegrationConfiguration> SchedulingIntegrationConfigurations => Set<SchedulingIntegrationConfiguration>();
    public DbSet<ExternalSchedulingResourceMapping> ExternalSchedulingResourceMappings => Set<ExternalSchedulingResourceMapping>();
    public DbSet<SchedulingAppointmentTypeDefinition> SchedulingAppointmentTypes => Set<SchedulingAppointmentTypeDefinition>();
    public DbSet<SchedulingProviderWorkingHours> SchedulingProviderWorkingHours => Set<SchedulingProviderWorkingHours>();
    public DbSet<SchedulingBlockedTime> SchedulingBlockedTimes => Set<SchedulingBlockedTime>();
    public DbSet<ExternalAppointmentReference> ExternalAppointmentReferences => Set<ExternalAppointmentReference>();
    public DbSet<SchedulingIntegrationEvent> SchedulingIntegrationEvents => Set<SchedulingIntegrationEvent>();
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
