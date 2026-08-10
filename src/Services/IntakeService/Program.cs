using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;

// IntakeService is the ONLY component intended to be exposed to the public
// internet. It authenticates and validates website booking requests and
// publishes a BookingRequestedEvent to Service Bus. It has NO database context
// and NO access to appointments or any PHI — a private consumer
// (SchedulingService) turns the event into an appointment.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Intake Service", Version = "v1" }));
builder.Services.AddHealthChecks();
builder.Services.AddEventPublishing(builder.Configuration);

// Rate limit the public endpoint per forwarded client IP.
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

app.MapPost("/api/public/booking-requests", async (
    PublicBookingRequest request,
    IConfiguration config,
    IEventPublisher publisher,
    HttpContext http) =>
{
    var section = config.GetSection("PublicBooking");
    if (!section.GetValue("Enabled", false))
        return Results.NotFound();

    var apiKey = section.GetValue<string>("ApiKey");
    if (string.IsNullOrWhiteSpace(apiKey) || !IntakeAuth.IsAuthorized(http, apiKey))
        return Results.Unauthorized();

    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name))
        errors["name"] = new[] { "Name is required." };
    if (string.IsNullOrWhiteSpace(request.Phone))
        errors["phone"] = new[] { "Phone is required." };

    // Require an unambiguous instant: UTC ("Z") or an explicit offset. A value
    // with no timezone (DateTimeKind.Unspecified) is rejected rather than
    // silently assumed to be UTC.
    if (request.PreferredStart == default)
        errors["preferredStart"] = new[] { "A preferred start time is required." };
    else if (request.PreferredStart.Kind == DateTimeKind.Unspecified)
        errors["preferredStart"] = new[] { "preferredStart must include a timezone (UTC 'Z' or an offset)." };

    var preferredStartUtc = request.PreferredStart.ToUniversalTime();
    if (!errors.ContainsKey("preferredStart") && preferredStartUtc <= DateTime.UtcNow)
        errors["preferredStart"] = new[] { "preferredStart must be in the future." };

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var evt = new BookingRequestedEvent(
        Name: request.Name,
        Phone: request.Phone,
        Email: request.Email,
        PreferredStartUtc: preferredStartUtc,
        DurationMinutes: request.DurationMinutes,
        Reason: request.Reason,
        Message: request.Message);

    await publisher.PublishAsync(evt, http.RequestAborted);

    // Accepted for asynchronous processing — staff confirm before finalizing.
    return Results.Accepted(value: new { status = "requested", eventId = evt.EventId });
})
.RequireRateLimiting("public-booking")
.WithTags("PublicBooking");

app.Run();

internal static class IntakeAuth
{
    /// <summary>
    /// Validates the request's API key against the configured value using a
    /// constant-time comparison. Accepts "Authorization: Bearer &lt;key&gt;" or
    /// "X-Api-Key: &lt;key&gt;".
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
}
