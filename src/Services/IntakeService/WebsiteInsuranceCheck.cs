using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

/// <summary>The website's "Check My Insurance" form, as posted by the site's server-side function.</summary>
public sealed record PublicInsuranceCheckRequest
{
    public string? RequestId { get; init; }
    public string? Name { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Carrier { get; init; }
    public string? Plan { get; init; }
    public string? RequestingAppointment { get; init; }
    public string? Source { get; init; }
    public string? Campaign { get; init; }
    public DateTime? CreatedAt { get; init; }
    public IReadOnlyDictionary<string, string>? Attribution { get; init; }
}

/// <summary>
/// Relays a website insurance check to the portal, where staff match the visitor to a
/// patient and email them the secure coverage link. Authenticated with the site's
/// PublicBooking key; nothing is stored here. The website form never asks for member
/// IDs or dates of birth, and this endpoint has no field to carry them.
/// </summary>
public static class WebsiteInsuranceCheckEndpoint
{
    public static void MapWebsiteInsuranceChecks(this WebApplication app) =>
        app.MapPost("/api/public/insurance-checks", Handle)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024))
            .RequireRateLimiting("public-booking")
            .WithTags("PublicBooking");

    public static async Task<IResult> Handle(PublicInsuranceCheckRequest request, IConfiguration config,
        IEventPublisher publisher, ServiceBusOptions serviceBus, TimeProvider time, ILoggerFactory loggers, HttpContext http)
    {
        var section = config.GetSection("PublicBooking");
        if (!section.GetValue("Enabled", false)) return Results.NotFound();
        var tenantId = IntakeAuth.ResolveTenant(http, section);
        if (tenantId is null) return Results.Unauthorized();

        var errors = Validate(request);
        var websiteRequestId = PublicBookingSanitizer.Text(request.RequestId, 128);
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(idempotencyKey) && websiteRequestId is not null) idempotencyKey = websiteRequestId;
        if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyKey.Length is < 8 or > 128)
            errors["Idempotency-Key"] = ["Idempotency-Key must be 8 to 128 characters."];
        if (websiteRequestId is not null && !string.Equals(websiteRequestId, idempotencyKey, StringComparison.Ordinal))
            errors["requestId"] = ["requestId must match Idempotency-Key when both are supplied."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        // Never accept what can't be delivered: a 503 sends the website to its email fallback.
        if (!serviceBus.IsConfigured)
            return Results.Problem(title: "Insurance checks are temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var now = time.GetUtcNow().UtcDateTime;
        var attribution = PublicBookingSanitizer.SanitizeAttribution(request.Attribution);
        var evt = ToEvent(request, tenantId, now, attribution, websiteRequestId,
            source: PublicBookingSanitizer.Text(request.Source, 100) ?? section.GetValue<string>("Source") ?? "PublicWebsite") with
        {
            // A resubmitted form is dropped by broker duplicate detection and the portal's unique key.
            EventId = string.IsNullOrEmpty(idempotencyKey)
                ? Guid.NewGuid()
                : Idempotency.CreateEventId($"{tenantId}\ninsurance-check", idempotencyKey)
        };

        try { await publisher.PublishAsync(evt, http.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("WebsiteInsuranceCheck").LogError("Could not relay insurance check {EventId} ({FailureKind}).",
                evt.EventId, ex.GetType().Name);
            return Results.Problem(title: "Insurance checks are temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Accepted(value: new { status = "received", eventId = evt.EventId });
    }

    public static Dictionary<string, string[]> Validate(PublicInsuranceCheckRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            errors["name"] = ["Name is required and must be 200 characters or fewer."];
        if (string.IsNullOrWhiteSpace(request.Carrier) || request.Carrier.Trim().Length > 120)
            errors["carrier"] = ["Carrier is required and must be 120 characters or fewer."];
        if (request.Plan?.Trim().Length > 120) errors["plan"] = ["plan must be 120 characters or fewer."];
        var phone = request.Phone?.Trim();
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(phone) && string.IsNullOrEmpty(email))
            errors["contact"] = ["A phone number or email is required."];
        if (!string.IsNullOrEmpty(phone) && (phone.Length > 30 || phone.Count(char.IsDigit) < 7))
            errors["phone"] = ["Phone must contain at least 7 digits and be 30 characters or fewer."];
        if (!string.IsNullOrEmpty(email) && (email.Length > 320 || !new EmailAddressAttribute().IsValid(email)))
            errors["email"] = ["Email must be a valid address and 320 characters or fewer."];
        if (request.RequestingAppointment is not null and not ("Yes" or "No"))
            errors["requestingAppointment"] = ["requestingAppointment must be Yes or No."];
        if (request.RequestId?.Length > 128) errors["requestId"] = ["requestId must be 128 characters or fewer."];
        if (request.Source?.Length > 100) errors["source"] = ["source must be 100 characters or fewer."];
        if (request.Campaign?.Length > 200) errors["campaign"] = ["campaign must be 200 characters or fewer."];
        return errors;
    }

    private static WebsiteInsuranceCheckEvent ToEvent(PublicInsuranceCheckRequest request, string tenantId, DateTime now,
        Dictionary<string, string> attribution, string? websiteRequestId, string source) =>
        new(tenantId, request.Name!.Trim(), request.Carrier!.Trim())
        {
            OccurredAt = now,
            Plan = PublicBookingSanitizer.Text(request.Plan, 120),
            Phone = PublicBookingSanitizer.Text(request.Phone, 30),
            Email = PublicBookingSanitizer.Text(request.Email, 320),
            RequestingAppointment = request.RequestingAppointment,
            WebsiteRequestId = websiteRequestId,
            Source = source,
            Campaign = PublicBookingSanitizer.Text(request.Campaign, 200),
            AttributionId = attribution.GetValueOrDefault("attribution_id"),
            SubmittedAtUtc = PublicBookingSanitizer.SubmittedAt(request.CreatedAt, now)
        };
}
