using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Contracts.Events;
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPatientAcquisitionService, PatientAcquisitionService>();
builder.Services.AddEventPublishing(builder.Configuration);

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
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SchedulingTenant", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !string.IsNullOrWhiteSpace(
            SchedulingIntegrationAdminApi.TenantId(context.User))));
    options.AddPolicy("SchedulingIntegrationAdmin", policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("Admin")
        .RequireAssertion(context => !string.IsNullOrWhiteSpace(
            SchedulingIntegrationAdminApi.TenantId(context.User))));
});

// Consume public booking-request events from Service Bus and turn them into
// (unconfirmed) appointments. Runs only when ServiceBus is configured; the
// consumer self-guards otherwise. Public booking traffic never reaches this
// service directly — the internet-facing IntakeService publishes the events.
builder.Services.AddHostedService<BookingRequestConsumer>();
builder.Services.AddHostedService<SchedulingService.Integrations.Zocdoc.ZocdocAvailabilityConsumer>();
builder.Services.AddHostedService<SchedulingService.Integrations.Zocdoc.ZocdocAppointmentWebhookConsumer>();
builder.Services.AddHostedService<SchedulingService.Integrations.Zocdoc.ZocdocAppointmentLifecycleConsumer>();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapSchedulingIntegrationAdminApi();

// Private, tenant-bound boundary used only by IntakeService. It returns a
// data-minimized website projection and revalidates opaque selections against
// the same canonical availability engine used by marketplace adapters.
app.MapPost("/api/internal/public-scheduling/availability", async (
    PublicSchedulingAvailabilityRequest request, HttpContext http, IConfiguration configuration,
    IPublicWebsiteSchedulingService service, CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingInternalAuth.ResolveTenant(http, configuration);
    if (tenantId is null) return Results.Unauthorized();
    try { return Results.Ok(await service.GetAsync(tenantId, request, cancellationToken)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
}).WithTags("InternalPublicScheduling");

// Versioned public availability projection. IntakeService forwards this to the
// internet edge at /api/public/v1/availability. Same canonical engine, same
// data-minimized codes; additionally carries the practice time zone and
// zone-offset timestamps.
app.MapPost("/api/internal/public-scheduling/availability/v1", async (
    PublicSchedulingAvailabilityRequest request, HttpContext http, IConfiguration configuration,
    IPublicWebsiteSchedulingService service, CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingInternalAuth.ResolveTenant(http, configuration);
    if (tenantId is null) return Results.Unauthorized();
    try { return Results.Ok(await service.GetPublishedAsync(tenantId, request, cancellationToken)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
}).WithTags("InternalPublicScheduling");

app.MapPost("/api/internal/public-scheduling/validate", async (
    ValidatePublicSlotRequest request, HttpContext http, IConfiguration configuration,
    IPublicWebsiteSchedulingService service, CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingInternalAuth.ResolveTenant(http, configuration);
    if (tenantId is null) return Results.Unauthorized();
    var selection = await service.ValidateAsync(tenantId, request.AvailabilityToken, request.PatientRelationship, cancellationToken);
    return selection is null ? Results.Conflict(new { message = "That time is no longer available." }) : Results.Ok(selection);
}).WithTags("InternalPublicScheduling");

app.MapPost("/api/internal/acquisition-events", async (
    PublicAcquisitionEvent input, HttpContext http, IConfiguration configuration,
    IPatientAcquisitionService service, CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingInternalAuth.ResolveTenant(http, configuration);
    if (tenantId is null) return Results.Unauthorized();
    try { return Results.Ok(new { accepted = await service.RecordWebsiteAsync(tenantId, input, cancellationToken) }); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["event"] = [ex.Message] }); }
}).WithTags("InternalAcquisition");

app.MapGet("/api/reports/patient-acquisition", async (
    DateTimeOffset from, DateTimeOffset to, string? source, string? appointmentIntent, string? landingPage,
    Guid? locationId, int? providerId, ClaimsPrincipal user, IPatientAcquisitionService service,
    CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    try { return Results.Ok(await service.GetDashboardAsync(tenantId, new(from, to, source, appointmentIntent, landingPage, locationId, providerId), cancellationToken)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = [ex.Message] }); }
}).RequireAuthorization("SchedulingIntegrationAdmin").WithTags("Reports");

// Staff appointment APIs contain PHI and always require a signed bearer token.
// Tenant context comes from the validated token, never request input or network location.
app.MapGet("/api/appointments", async (SchedulingDbContext db, ClaimsPrincipal user, DateTime? from, DateTime? to) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    var query = db.Appointments.Where(a => a.TenantId == tenantId);
    if (from.HasValue) query = query.Where(a => a.StartTime >= from.Value);
    if (to.HasValue) query = query.Where(a => a.StartTime <= to.Value);
    return Results.Ok(await query.OrderBy(a => a.StartTime).Take(100).ToListAsync());
}).RequireAuthorization("SchedulingTenant").WithTags("Appointments");

app.MapGet("/api/appointments/{id:guid}", async (Guid id, SchedulingDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    var apt = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId);
    return apt is not null ? Results.Ok(apt) : Results.NotFound();
}).RequireAuthorization("SchedulingTenant").WithTags("Appointments");

app.MapPost("/api/appointments", async (
    CreateAppointmentRequest request, ClaimsPrincipal user, SchedulingDbContext db,
    IEventPublisher events, ILogger<Program> logger) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user);
    if (string.IsNullOrWhiteSpace(tenantId)) return Results.Unauthorized();
    var apt = new Appointment
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
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
    try
    {
        await events.PublishAsync(new SchedulingAvailabilityChangedEvent(
            tenantId, apt.ProviderId, apt.StartTime, apt.EndTime, "AppointmentScheduled"));
    }
    catch (Exception ex)
    {
        // The local appointment is authoritative. External availability is eventually reconciled.
        logger.LogError(ex, "Could not enqueue external availability reconciliation for tenant {TenantId}, provider {ProviderId}",
            tenantId, apt.ProviderId);
    }
    return Results.Created($"/api/appointments/{apt.Id}", apt);
}).RequireAuthorization("SchedulingTenant").WithTags("Appointments");

app.MapPut("/api/appointments/{id:guid}/lifecycle", async (
    Guid id, SchedulingService.Integrations.Zocdoc.AppointmentLifecycleCommand command,
    ClaimsPrincipal user, SchedulingService.Integrations.Zocdoc.IAppointmentLifecycleService service,
    CancellationToken cancellationToken) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user);
    if (string.IsNullOrWhiteSpace(tenantId)) return Results.Unauthorized();
    try { return Results.Ok(await service.ApplyLocalAsync(tenantId, id, command, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["lifecycle"] = [ex.Message] }); }
}).RequireAuthorization("SchedulingTenant").WithTags("Appointments");

app.MapGet("/api/booking-requests", async (SchedulingDbContext db, ClaimsPrincipal user, string? status) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
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
}).RequireAuthorization("SchedulingTenant").WithTags("BookingRequests");

app.MapGet("/api/booking-requests/{id:guid}", async (Guid id, SchedulingDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    var request = await db.BookingRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId);
    return request is null ? Results.NotFound() : Results.Ok(request.ToDto());
}).RequireAuthorization("SchedulingTenant").WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/match-patient", async (
    Guid id, MatchBookingPatientRequest match, SchedulingDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    try { return Results.Ok((await new BookingRequestWorkflow(db).MatchPatientAsync(id, tenantId, match)).ToDto()); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["patientId"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).RequireAuthorization("SchedulingTenant").WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/status", async (
    Guid id, ChangeBookingRequestStatusRequest change, SchedulingDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    try { return Results.Ok((await new BookingRequestWorkflow(db).ChangeStatusAsync(id, tenantId, change)).ToDto()); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).RequireAuthorization("SchedulingTenant").WithTags("BookingRequests");

app.MapPost("/api/booking-requests/{id:guid}/approve", async (
    Guid id, ApproveBookingRequest approval, SchedulingDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = SchedulingIntegrationAdminApi.TenantId(user)!;
    try
    {
        var result = await new BookingRequestWorkflow(db).ApproveAsync(id, tenantId, approval);
        return Results.Ok(new { bookingRequest = result.Request.ToDto(), appointmentId = result.Appointment.Id, created = result.Created });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
}).RequireAuthorization("SchedulingTenant").WithTags("BookingRequests");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
    await db.Database.EnsureCreatedAsync();
    await BookingRequestSchema.EnsureAsync(db);
    await SchedulingIntegrationSchema.EnsureAsync(db);
    await SchedulingAvailabilitySchema.EnsureAsync(db);
    await PatientAcquisitionSchema.EnsureAsync(db);
}

app.Run();

public partial class Program { }

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

public sealed record ValidatePublicSlotRequest(string AvailabilityToken, PatientRelationship PatientRelationship);

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
    public DbSet<SchedulingAvailabilitySyncState> SchedulingAvailabilitySyncStates => Set<SchedulingAvailabilitySyncState>();
    public DbSet<PatientAcquisitionEvent> PatientAcquisitionEvents => Set<PatientAcquisitionEvent>();
}
