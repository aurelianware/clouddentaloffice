using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;

// IntakeService is the ONLY component intended to be exposed to the public
// internet. It authenticates and validates website booking requests and
// publishes a BookingRequestedEvent to Service Bus. It has NO database context
// and no read access to patient, clinical, or scheduling databases. It accepts
// only the minimum contact and preference data required for appointment intake.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ZocdocWebhookMetrics>();
builder.Services.AddHttpClient<IPublicSchedulingClient, PublicSchedulingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:SchedulingService"] ?? "http://scheduling-service:5102/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

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
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => PublicBookingRateLimits.Create());
    });
    options.AddPolicy("zocdoc-webhooks", httpContext =>
    {
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
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

app.MapGet("/api/public/availability", async (
    PatientRelationship patientRelationship, DateTimeOffset from, DateTimeOffset to,
    string? appointmentType, string? provider, string? location,
    IConfiguration config, IPublicSchedulingClient scheduling, HttpContext http) =>
{
    var section = config.GetSection("PublicBooking");
    if (!section.GetValue("Enabled", false)) return Results.NotFound();
    var tenantId = IntakeAuth.ResolveTenant(http, section);
    if (tenantId is null) return Results.Unauthorized();
    if (patientRelationship == PatientRelationship.Unknown || to <= from || to - from > TimeSpan.FromDays(31))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Choose New or Existing and a valid date range of 31 days or less."] });
    try
    {
        return Results.Ok(await scheduling.GetAvailabilityAsync(tenantId,
            new(patientRelationship, from, to, appointmentType, provider, location), http.RequestAborted));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Problem(title: "Availability is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("public-booking").WithTags("PublicBooking");

app.MapPost("/api/integrations/zocdoc/{integrationId}/webhooks", async (
    string integrationId, HttpContext http, IConfiguration configuration,
    IEventPublisher publisher, ServiceBusOptions serviceBus, TimeProvider timeProvider,
    ZocdocWebhookMetrics metrics) =>
{
    metrics.Received.Add(1);
    var integration = configuration.GetSection("ZocdocWebhooks:Integrations").GetChildren()
        .Select(x => x.Get<ZocdocWebhookIntegrationOptions>())
        .FirstOrDefault(x => x is not null && string.Equals(x.IntegrationId, integrationId, StringComparison.Ordinal));
    if (integration is not { Enabled: true } || string.IsNullOrWhiteSpace(integration.TenantId) ||
        string.IsNullOrWhiteSpace(integration.WebhookSecret)) return Results.NotFound();
    if (!serviceBus.IsConfigured) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    byte[] body;
    try
    {
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length is 0 or > 1_048_576)
        {
            metrics.ValidationFailures.Add(1);
            return Results.BadRequest();
        }
        body = buffer.ToArray();
    }
    catch
    {
        metrics.ValidationFailures.Add(1);
        return Results.BadRequest();
    }

    if (!ZocdocWebhookSignatureVerifier.Verify(body,
        http.Request.Headers["webhook-timestamp"].ToString(),
        http.Request.Headers["webhook-signature"].ToString(),
        integration.WebhookSecret, timeProvider.GetUtcNow()))
    {
        metrics.ValidationFailures.Add(1);
        return Results.Unauthorized();
    }

    ZocdocWebhookEnvelope? webhook;
    try { webhook = JsonSerializer.Deserialize<ZocdocWebhookEnvelope>(body); }
    catch (JsonException) { metrics.ValidationFailures.Add(1); return Results.BadRequest(); }
    var appointment = webhook?.Data?.AppointmentData;
    if (webhook?.EventType != "appointment_updated" || webhook.Data?.DataType != "appointment_data" ||
        appointment is null || string.IsNullOrWhiteSpace(appointment.AppointmentId) ||
        appointment.AppointmentUpdateType is not ("created" or "updated" or "cancelled") ||
        appointment.UpdatedAt == default)
    {
        metrics.ValidationFailures.Add(1);
        return Results.BadRequest();
    }

    var externalEventId = $"{appointment.AppointmentId}:{appointment.UpdatedAt:O}:{appointment.AppointmentUpdateType}";
    await publisher.PublishAsync(new ZocdocAppointmentWebhookEvent(
        integration.TenantId, externalEventId, appointment.AppointmentId, appointment.AppointmentUpdateType)
        { ExternalUpdatedAt = appointment.UpdatedAt.UtcDateTime },
        http.RequestAborted);
    return Results.Accepted();
}).RequireRateLimiting("zocdoc-webhooks").WithTags("ZocdocWebhooks");

app.MapPost("/api/public/booking-requests", async (
    PublicBookingRequest request,
    IConfiguration config,
    IEventPublisher publisher,
    ServiceBusOptions serviceBus,
    ILoggerFactory loggerFactory,
    IPublicSchedulingClient scheduling,
    HttpContext http) =>
{
    var section = config.GetSection("PublicBooking");
    if (!section.GetValue("Enabled", false))
        return Results.NotFound();

    var tenantId = IntakeAuth.ResolveTenant(http, section);
    if (tenantId is null)
        return Results.Unauthorized();
    if (section.GetValue("RequireAvailabilitySelection", false) && string.IsNullOrWhiteSpace(request.AvailabilityToken))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["availabilityToken"] = ["Choose one of the currently available appointment times."] });

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

    var websiteRequestId = PublicBookingSanitizer.Text(request.RequestId, 128);
    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
    if (string.IsNullOrEmpty(idempotencyKey) && websiteRequestId is not null)
        idempotencyKey = websiteRequestId;
    if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyKey.Length is < 8 or > 128)
        errors["Idempotency-Key"] = ["Idempotency-Key must be 8 to 128 characters."];
    if (websiteRequestId is not null && !string.Equals(websiteRequestId, idempotencyKey, StringComparison.Ordinal))
        errors["requestId"] = ["requestId must match Idempotency-Key when both are supplied."];

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    ValidatedPublicSchedulingSelection? selection = null;
    if (!string.IsNullOrWhiteSpace(request.AvailabilityToken))
    {
        try { selection = await scheduling.ValidateAsync(tenantId, request.AvailabilityToken, request.PatientRelationship, http.RequestAborted); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Results.Problem(title: "Booking is temporarily unavailable. Please try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (selection is null)
            return Results.Conflict(new { message = "That appointment time is no longer available. Please choose another time." });
        if (request.PreferredStart.ToUniversalTime() != selection.Start.UtcDateTime)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["preferredStart"] = ["The selected time does not match the availability selection."] });
        preferredStartUtc = selection.Start.UtcDateTime;
    }

    var attribution = PublicBookingSanitizer.SanitizeAttribution(request.Attribution);
    var evt = new BookingRequestedEvent(
        Name: request.Name.Trim(),
        Phone: request.Phone.Trim(),
        Email: string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
        PreferredStartUtc: preferredStartUtc,
        DurationMinutes: selection is null ? request.DurationMinutes : (int)(selection.End - selection.Start).TotalMinutes,
        Reason: string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
        Message: string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
        PatientRelationship: request.PatientRelationship,
        TenantId: tenantId,
        Source: PublicBookingSanitizer.Text(request.Source, 100) ?? section.GetValue<string>("Source") ?? "PublicWebsite",
        SourceReference: null)
    {
        ContractVersion = selection is null ? 2 : 3,
        EventId = string.IsNullOrEmpty(idempotencyKey)
            ? Guid.NewGuid()
            : Idempotency.CreateEventId(tenantId, idempotencyKey),
        WebsiteRequestId = websiteRequestId,
        PreferredContact = PublicBookingSanitizer.Text(request.PreferredContact, 20),
        AlternateStartUtc = request.AlternateStart?.ToUniversalTime(),
        InsuranceIntent = PublicBookingSanitizer.Text(request.InsuranceIntent, 20),
        InsuranceCarrier = PublicBookingSanitizer.Text(request.InsuranceCarrier, 120),
        Campaign = PublicBookingSanitizer.Text(request.Campaign, 200),
        AttributionId = attribution.GetValueOrDefault("attribution_id"),
        AttributionMetadata = attribution,
        SubmittedAtUtc = PublicBookingSanitizer.SubmittedAt(request.CreatedAt, DateTime.UtcNow),
        SourceReference = string.IsNullOrWhiteSpace(request.AvailabilityToken) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.AvailabilityToken)))[..32],
        RequestedProviderId = selection?.ProviderId,
        RequestedLocationId = selection?.LocationId,
        RequestedAppointmentTypeId = selection?.AppointmentTypeId
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
        var email = request.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(email) && (email.Length > 320 || !new EmailAddressAttribute().IsValid(email)))
            errors["email"] = ["Email must be a valid address and 320 characters or fewer."];
        if (request.DurationMinutes is < 15 or > 240) errors["durationMinutes"] = ["Duration must be between 15 and 240 minutes."];
        if (request.Reason?.Length > 500) errors["reason"] = ["Reason must be 500 characters or fewer."];
        if (request.Message?.Length > 2000) errors["message"] = ["Message must be 2000 characters or fewer."];
        if (request.RequestId?.Length > 128) errors["requestId"] = ["requestId must be 128 characters or fewer."];
        if (request.AvailabilityToken?.Length > 4096) errors["availabilityToken"] = ["Availability selection is invalid."];
        if (request.PreferredContact is not null && request.PreferredContact is not ("Phone" or "Text" or "Email"))
            errors["preferredContact"] = ["preferredContact must be Phone, Text, or Email."];
        if (request.InsuranceIntent is not null && request.InsuranceIntent is not ("Yes" or "No" or "Not sure"))
            errors["insuranceIntent"] = ["insuranceIntent must be Yes, No, or Not sure."];
        if (request.InsuranceCarrier?.Length > 120) errors["insuranceCarrier"] = ["insuranceCarrier must be 120 characters or fewer."];
        if (request.Source?.Length > 100) errors["source"] = ["source must be 100 characters or fewer."];
        if (request.Campaign?.Length > 200) errors["campaign"] = ["campaign must be 200 characters or fewer."];
        if (!Enum.IsDefined(request.PatientRelationship) || request.PatientRelationship == PatientRelationship.Unknown)
            errors["patientRelationship"] = ["Patient relationship must be New or Existing."];
        if (request.PreferredStart == default) errors["preferredStart"] = ["A preferred start time is required."];
        else if (request.PreferredStart.Kind == DateTimeKind.Unspecified)
            errors["preferredStart"] = ["preferredStart must include a timezone (UTC 'Z' or an offset)."];
        else if (request.PreferredStart.ToUniversalTime() <= utcNow.AddMinutes(5))
            errors["preferredStart"] = ["preferredStart must be at least 5 minutes in the future."];
        else if (request.PreferredStart.ToUniversalTime() > utcNow.AddYears(1))
            errors["preferredStart"] = ["preferredStart must be within one year."];
        if (request.AlternateStart.HasValue && request.AlternateStart.Value.Kind == DateTimeKind.Unspecified)
            errors["alternateStart"] = ["alternateStart must include a timezone."];
        return errors;
    }
}

public static class PublicBookingRateLimits
{
    public static FixedWindowRateLimiterOptions Create() => new()
    {
        PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    };
}

public static class PublicBookingSanitizer
{
    private static readonly HashSet<string> AllowedAttributionKeys = new(StringComparer.Ordinal)
    { "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term", "landing_page", "referrer", "attribution_id" };

    public static string? Text(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    public static Dictionary<string, string> SanitizeAttribution(IReadOnlyDictionary<string, string>? values) =>
        values?.Where(pair => AllowedAttributionKeys.Contains(pair.Key))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, Text(pair.Value, 200) ?? string.Empty))
            .Where(pair => pair.Value.Length > 0)
            .Take(8)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        ?? [];

    public static DateTime SubmittedAt(DateTime? value, DateTime utcNow)
    {
        if (!value.HasValue || value.Value.Kind == DateTimeKind.Unspecified) return utcNow;
        var utc = value.Value.ToUniversalTime();
        return utc > utcNow.AddMinutes(5) || utc < utcNow.AddDays(-7) ? utcNow : utc;
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

public sealed class ZocdocWebhookIntegrationOptions
{
    public string IntegrationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public sealed class ZocdocWebhookMetrics : IDisposable
{
    private readonly Meter _meter = new("CloudDentalOffice.Intake.Zocdoc");
    public Counter<long> Received { get; }
    public Counter<long> ValidationFailures { get; }

    public ZocdocWebhookMetrics()
    {
        Received = _meter.CreateCounter<long>("intake.zocdoc.webhook.received");
        ValidationFailures = _meter.CreateCounter<long>("intake.zocdoc.webhook.validation_failures");
    }

    public void Dispose() => _meter.Dispose();
}

public sealed record ZocdocWebhookEnvelope(
    [property: JsonPropertyName("data")] ZocdocWebhookData? Data,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("webhook_timestamp")] DateTimeOffset WebhookTimestamp);
public sealed record ZocdocWebhookData(
    [property: JsonPropertyName("appointment_data")] ZocdocWebhookAppointment? AppointmentData,
    [property: JsonPropertyName("data_type")] string? DataType);
public sealed record ZocdocWebhookAppointment(
    [property: JsonPropertyName("appointment_id")] string AppointmentId,
    [property: JsonPropertyName("appointment_updated_timestamp")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("appointment_update_type")] string AppointmentUpdateType);

public static class ZocdocWebhookSignatureVerifier
{
    public static bool Verify(ReadOnlySpan<byte> rawBody, string timestampHeader, string signatureHeader,
        string base64Secret, DateTimeOffset now)
    {
        if (!long.TryParse(timestampHeader, out var timestamp)) return false;
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - signedAt).Duration() > TimeSpan.FromMinutes(5)) return false;
        byte[] key;
        try { key = Convert.FromBase64String(base64Secret); }
        catch (FormatException) { return false; }
        if (key.Length == 0) return false;

        var prefix = Encoding.UTF8.GetBytes(timestampHeader + ".");
        var signedPayload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signedPayload, 0);
        rawBody.CopyTo(signedPayload.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(key, signedPayload);
        foreach (var versioned in signatureHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = versioned.IndexOf(':');
            if (separator <= 0 || !versioned[..separator].Equals("v1", StringComparison.Ordinal)) continue;
            try
            {
                var supplied = Convert.FromBase64String(versioned[(separator + 1)..]);
                if (supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected))
                    return true;
            }
            catch (FormatException) { }
        }
        return false;
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
