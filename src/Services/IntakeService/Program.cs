using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;

// IntakeService is the ONLY component intended to be exposed to the public
// internet. It authenticates and validates website booking requests and
// publishes a BookingRequestedEvent to Service Bus. It has NO database context
// and no read access to patient, clinical, or scheduling databases. It accepts
// only the minimum contact and preference data required for appointment intake.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<PublicBookingOptions>()
    .Bind(builder.Configuration.GetSection(PublicBookingOptions.SectionName))
    .Validate(o => !o.Enabled || o.Clients.Any(c =>
        !string.IsNullOrWhiteSpace(c.TenantId) && c.ApiKey?.Length >= 32),
        "Enabled public booking requires at least one client with a tenant ID and a 32+ character API key.")
    .ValidateOnStart();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Intake Service", Version = "v1" }));
builder.Services.AddHealthChecks();
builder.Services.AddEventPublishing(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var value in builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? [])
        if (IPAddress.TryParse(value, out var address)) options.KnownProxies.Add(address);
});

// Rate limit by the connection address after ASP.NET Core has accepted forwarded
// headers only from explicitly configured trusted proxies.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-booking", httpContext =>
    {
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
app.UseForwardedHeaders();
app.UseRateLimiter();
app.MapHealthChecks("/health");

app.MapPost("/api/public/booking-requests", async (
    PublicBookingRequest request,
    IConfiguration config,
    IEventPublisher publisher,
    ServiceBusOptions serviceBus,
    ILoggerFactory loggerFactory,
    HttpContext http) =>
{
    var section = config.GetSection("PublicBooking");
    if (!section.GetValue("Enabled", false))
        return Results.NotFound();

    var tenantId = IntakeAuth.ResolveTenant(http, section);
    if (tenantId is null)
        return Results.Unauthorized();

    // Don't falsely accept a booking we can't deliver: if Service Bus isn't
    // configured, the publisher would drop the event. Return 503 so the caller
    // falls back to its own delivery path instead.
    if (!serviceBus.IsConfigured)
    {
        loggerFactory.CreateLogger("PublicBooking").LogError(
            "PublicBooking is enabled but ServiceBus is not configured; refusing the request.");
        return Results.Problem(
            title: "Booking is temporarily unavailable. Please try again shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var errors = PublicBookingValidator.Validate(request, DateTime.UtcNow);
    var preferredStartUtc = request.PreferredStart.ToUniversalTime();

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
    if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyKey.Length is < 8 or > 128)
        errors["Idempotency-Key"] = ["Idempotency-Key must be 8 to 128 characters."];

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var evt = new BookingRequestedEvent(
        Name: request.Name.Trim(),
        Phone: request.Phone.Trim(),
        Email: string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
        PreferredStartUtc: preferredStartUtc,
        DurationMinutes: request.DurationMinutes,
        Reason: string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
        Message: string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
        PatientRelationship: request.PatientRelationship,
        TenantId: tenantId,
        Source: section.GetValue<string>("Source") ?? "PublicWebsite",
        SourceReference: null)
    {
        EventId = string.IsNullOrEmpty(idempotencyKey)
            ? Guid.NewGuid()
            : Idempotency.CreateEventId(tenantId, idempotencyKey)
    };

    try
    {
        await publisher.PublishAsync(evt, http.RequestAborted);
    }
    catch (Exception ex)
    {
        // The broker is unreachable. Return 503 so the caller (the website)
        // can fall back to its own delivery path (e.g. email) instead of
        // treating the booking as accepted.
        loggerFactory.CreateLogger("PublicBooking").LogError(ex, "Failed to publish booking event {EventId}.", evt.EventId);
        return Results.Problem(
            title: "Booking is temporarily unavailable. Please try again shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Accepted for asynchronous processing — staff confirm before finalizing.
    return Results.Accepted(value: new { status = "requested", eventId = evt.EventId });
})
.RequireRateLimiting("public-booking")
.WithTags("PublicBooking");

app.Run();

public static class PublicBookingValidator
{
    public static Dictionary<string, string[]> Validate(PublicBookingRequest request, DateTime utcNow)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) errors["name"] = ["Name is required and must be 200 characters or fewer."];
        if (string.IsNullOrWhiteSpace(request.Phone) || request.Phone.Trim().Length > 30 || request.Phone.Count(char.IsDigit) < 7)
            errors["phone"] = ["Phone must contain at least 7 digits and be 30 characters or fewer."];
        if (!string.IsNullOrWhiteSpace(request.Email) && (request.Email.Length > 320 || !new EmailAddressAttribute().IsValid(request.Email)))
            errors["email"] = ["Email must be a valid address and 320 characters or fewer."];
        if (request.DurationMinutes is < 15 or > 240) errors["durationMinutes"] = ["Duration must be between 15 and 240 minutes."];
        if (request.Reason?.Length > 500) errors["reason"] = ["Reason must be 500 characters or fewer."];
        if (request.Message?.Length > 2000) errors["message"] = ["Message must be 2000 characters or fewer."];
        if (!Enum.IsDefined(request.PatientRelationship) || request.PatientRelationship == PatientRelationship.Unknown)
            errors["patientRelationship"] = ["Patient relationship must be New or Existing."];
        if (request.PreferredStart == default) errors["preferredStart"] = ["A preferred start time is required."];
        else if (request.PreferredStart.Kind == DateTimeKind.Unspecified)
            errors["preferredStart"] = ["preferredStart must include a timezone (UTC 'Z' or an offset)."];
        else if (request.PreferredStart.ToUniversalTime() <= utcNow.AddMinutes(5))
            errors["preferredStart"] = ["preferredStart must be at least 5 minutes in the future."];
        else if (request.PreferredStart.ToUniversalTime() > utcNow.AddYears(1))
            errors["preferredStart"] = ["preferredStart must be within one year."];
        return errors;
    }
}

public static class Idempotency
{
    public static Guid CreateEventId(string tenantId, string key)
    {
        if (key.Length is < 8 or > 128) throw new ArgumentException("Idempotency-Key must be 8 to 128 characters.", nameof(key));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed class PublicBookingOptions
{
    public const string SectionName = "PublicBooking";
    public bool Enabled { get; set; }
    public List<PublicBookingClient> Clients { get; set; } = [];
}

public sealed class PublicBookingClient
{
    public string TenantId { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
}

public static class IntakeAuth
{
    public static string? ResolveTenant(HttpContext http, IConfigurationSection section)
    {
        var provided = GetProvidedKey(http);
        if (string.IsNullOrEmpty(provided)) return null;

        foreach (var client in section.GetSection("Clients").GetChildren())
        {
            var expected = client.GetValue<string>("ApiKey");
            var tenantId = client.GetValue<string>("TenantId");
            if (!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(tenantId) && ConstantTimeEquals(provided, expected))
                return tenantId;
        }

        var legacyKey = section.GetValue<string>("ApiKey");
        if (!string.IsNullOrWhiteSpace(legacyKey) && ConstantTimeEquals(provided, legacyKey))
            return section.GetValue("TenantId", "default");
        return null;
    }

    /// <summary>
    /// Validates the request's API key against the configured value using a
    /// constant-time comparison. Accepts "Authorization: Bearer &lt;key&gt;" or
    /// "X-Api-Key: &lt;key&gt;".
    /// </summary>
    public static bool IsAuthorized(HttpContext http, string expectedKey)
    {
        var provided = GetProvidedKey(http);
        return provided is not null && ConstantTimeEquals(provided, expectedKey);
    }

    private static string? GetProvidedKey(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth["Bearer ".Length..].Trim();
        var apiKey = http.Request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    private static bool ConstantTimeEquals(string provided, string expectedKey)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

public partial class Program { }
