using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Events;

public sealed class StripeWebhookAccountOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ConnectedAccountId { get; set; } = string.Empty;
    public bool LiveMode { get; set; }
    public bool Enabled { get; set; }
}

public sealed class StripeWebhookMetrics : IDisposable
{
    private readonly Meter _meter = new("CloudDentalOffice.Intake.Stripe", "1.0");
    public Counter<long> Received { get; }
    public Counter<long> Persisted { get; }
    public Counter<long> ValidationFailures { get; }

    public StripeWebhookMetrics()
    {
        Received = _meter.CreateCounter<long>("stripe.events.received");
        Persisted = _meter.CreateCounter<long>("stripe.events.persisted");
        ValidationFailures = _meter.CreateCounter<long>("stripe.webhook.validation_failures");
    }

    public void Dispose() => _meter.Dispose();
}

public static class StripeWebhookSignatureVerifier
{
    public static bool Verify(ReadOnlySpan<byte> body, string header, string secret,
        DateTimeOffset now, TimeSpan tolerance)
    {
        if (body.IsEmpty || string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret)) return false;
        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var component in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = component.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                timestamp = parsed;
            else if (pair[0] == "v1")
            {
                try { signatures.Add(Convert.FromHexString(pair[1])); }
                catch (FormatException) { }
            }
        }
        if (!timestamp.HasValue || signatures.Count == 0) return false;
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value); }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - signedAt).Duration() > tolerance) return false;

        var prefix = Encoding.UTF8.GetBytes($"{timestamp.Value}.");
        var signedPayload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedPayload);
        return signatures.Any(signature => signature.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(signature, expected));
    }
}

public static class StripeWebhookEndpoint
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "checkout.session.completed",
        "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed",
        "refund.created",
        "refund.updated",
        "refund.failed"
    };

    public static void MapStripeWebhook(this WebApplication app) =>
        app.MapPost("/api/integrations/stripe/webhooks", HandleAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_048_576))
            .RequireRateLimiting("stripe-webhooks").WithTags("StripeWebhooks");

    private static async Task<IResult> HandleAsync(HttpContext http, IConfiguration configuration,
        IIntegrationInbox inbox, TimeProvider timeProvider, StripeWebhookMetrics metrics)
    {
        metrics.Received.Add(1);
        var section = configuration.GetSection("StripeWebhooks");
        var secret = section["EndpointSecret"];
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith("whsec_", StringComparison.Ordinal))
            return Results.NotFound();
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            if (buffer.Length is 0 or > 1_048_576) return Invalid(metrics);
            body = buffer.ToArray();
        }
        catch { return Invalid(metrics); }

        var tolerance = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("ToleranceSeconds", 300), 1, 900));
        if (!StripeWebhookSignatureVerifier.Verify(body, http.Request.Headers["Stripe-Signature"].ToString(),
                secret, timeProvider.GetUtcNow(), tolerance))
        {
            metrics.ValidationFailures.Add(1);
            return Results.Unauthorized();
        }

        IntegrationEvent integrationEvent;
        string tenantId;
        string externalEventId;
        string subject;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var eventId = RequiredString(root, "id");
            var eventType = RequiredString(root, "type");
            if (!SupportedEvents.Contains(eventType)) return Results.Ok();
            var accountId = RequiredString(root, "account");
            var liveMode = root.GetProperty("livemode").GetBoolean();
            var account = section.GetSection("Accounts").GetChildren()
                .Select(x => x.Get<StripeWebhookAccountOptions>())
                .SingleOrDefault(x => x is { Enabled: true } && x.ConnectedAccountId == accountId && x.LiveMode == liveMode);
            if (account is null || string.IsNullOrWhiteSpace(account.TenantId)) return Results.NotFound();
            tenantId = account.TenantId;
            externalEventId = eventId;
            var created = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("created").GetInt64()).UtcDateTime;
            var data = root.GetProperty("data").GetProperty("object");
            if (eventType.StartsWith("refund.", StringComparison.Ordinal))
            {
                var reference = OptionalMetadata(data, "refund_reference");
                integrationEvent = new StripeRefundWebhookEvent(account.TenantId, eventId, eventType, accountId,
                    RequiredString(data, "id"), OptionalId(data, "payment_intent"), reference,
                    data.GetProperty("amount").GetInt64(), RequiredString(data, "currency").ToUpperInvariant(),
                    RequiredString(data, "status"), liveMode) { OccurredAt = created };
                subject = nameof(StripeRefundWebhookEvent);
            }
            else
            {
                var reference = OptionalMetadata(data, "payment_reference");
                var sessionId = RequiredString(data, "id");
                if (string.IsNullOrWhiteSpace(reference)) return Invalid(metrics);
                integrationEvent = new StripePaymentWebhookEvent(account.TenantId, eventId, eventType, accountId,
                    sessionId, OptionalId(data, "payment_intent"), reference,
                    data.GetProperty("amount_total").GetInt64(), RequiredString(data, "currency").ToUpperInvariant(),
                    RequiredString(data, "payment_status"), liveMode) { OccurredAt = created };
                subject = nameof(StripePaymentWebhookEvent);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Invalid(metrics);
        }

        try
        {
            await inbox.PersistAsync(tenantId, "Stripe", externalEventId, subject, integrationEvent, http.RequestAborted);
            metrics.Persisted.Add(1);
            return Results.Accepted();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StripeInbox")
                .LogError("Could not durably accept Stripe event {ExternalEventId}; failure {FailureKind}.",
                    externalEventId, ex.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Invalid(StripeWebhookMetrics metrics)
    {
        metrics.ValidationFailures.Add(1);
        return Results.BadRequest();
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new JsonException();

    private static string? OptionalId(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static string? OptionalMetadata(JsonElement element, string property) =>
        element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
