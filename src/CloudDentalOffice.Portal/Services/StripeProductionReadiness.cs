using System.Net.Http.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class StripeReadinessOptions
{
    public const string SectionName = "Payments:StripeReadiness";
    public string? IntakeServiceBaseUrl { get; set; }
    public string? IntakeServiceKey { get; set; }
    public int WebhookHealthyWithinHours { get; set; } = 72;
}

public sealed record StripeInboxStatus(int Received, int Publishing, int Published, int Failed,
    DateTime? OldestPendingAt, bool Available);

public interface IStripeInboxStatusClient
{
    Task<StripeInboxStatus> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class StripeInboxStatusClient(HttpClient http, IOptions<StripeReadinessOptions> options,
    ILogger<StripeInboxStatusClient> logger) : IStripeInboxStatusClient
{
    public async Task<StripeInboxStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.IntakeServiceBaseUrl) ||
            string.IsNullOrWhiteSpace(settings.IntakeServiceKey) ||
            !Uri.TryCreate(settings.IntakeServiceBaseUrl, UriKind.Absolute, out var baseUri))
            return new(0, 0, 0, 0, null, false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(baseUri, "/api/internal/integration-inbox/status?channel=Stripe"));
            request.Headers.TryAddWithoutValidation("X-CDO-Service-Key", settings.IntakeServiceKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(0, 0, 0, 0, null, false);
            var status = await response.Content.ReadFromJsonAsync<StripeInboxStatus>(cancellationToken);
            return status is null ? new(0, 0, 0, 0, null, false) : status with { Available = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Stripe inbox readiness could not be retrieved; failure {FailureKind}.",
                ex.GetType().Name);
            return new(0, 0, 0, 0, null, false);
        }
    }
}

public sealed record StripeProductionReadiness(
    PaymentProcessorEnvironment Environment, bool Connected, bool ChargesEnabled, bool PayoutsEnabled,
    bool WebhookHealthy, DateTime? LastWebhookEventAt, DateTime? LastSuccessfulPaymentEventAt,
    int PendingInboxCount, int FailedInboxCount, bool InboxStatusAvailable,
    DateTime? LastReconciliationAt, string ReconciliationStatus, bool PilotReady,
    IReadOnlyList<string> Blockers);

public interface IStripeProductionReadinessService
{
    Task<StripeProductionReadiness> GetAsync(string tenantId, CancellationToken cancellationToken = default);
}

public sealed class StripeProductionReadinessService(CloudDentalDbContext db, ITenantProvider tenantProvider,
    IStripeInboxStatusClient inboxClient, IOptions<StripeReadinessOptions> options, TimeProvider clock)
    : IStripeProductionReadinessService
{
    public async Task<StripeProductionReadiness> GetAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Provider == PaymentProcessorProvider.Stripe,
                cancellationToken);
        var lastEvent = await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Processor == PaymentProcessorProvider.Stripe && x.ProcessedAt != null)
            .MaxAsync(x => (DateTime?)x.ProcessedAt, cancellationToken);
        var lastPayment = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.Status == PaymentStatus.Succeeded)
            .MaxAsync(x => (DateTime?)x.UpdatedAt, cancellationToken);
        var inbox = await inboxClient.GetAsync(cancellationToken);
        var connected = configuration is { Enabled: true, ConnectedMerchantReference.Length: > 0 };
        var webhookHealthy = inbox.Available && inbox.Failed == 0 && lastEvent.HasValue &&
            clock.GetUtcNow().UtcDateTime - lastEvent.Value <=
                TimeSpan.FromHours(Math.Max(1, options.Value.WebhookHealthyWithinHours));
        var blockers = new List<string>();
        if (!connected) blockers.Add("Stripe is not connected and enabled.");
        if (configuration?.ChargesEnabled != true) blockers.Add("Stripe charges are not enabled.");
        if (configuration?.PayoutsEnabled != true) blockers.Add("Stripe payouts are not enabled.");
        if (!inbox.Available) blockers.Add("Webhook inbox status is unavailable.");
        else if (!webhookHealthy) blockers.Add(inbox.Failed > 0
            ? "The webhook inbox has failed events."
            : "No recent successful Stripe webhook event has been processed.");
        if (configuration?.LastReconciliationStatusCode != "clean")
            blockers.Add("A clean reconciliation has not been recorded.");

        return new(configuration?.Environment ?? PaymentProcessorEnvironment.Sandbox, connected,
            configuration?.ChargesEnabled == true, configuration?.PayoutsEnabled == true, webhookHealthy,
            lastEvent, lastPayment, inbox.Received + inbox.Publishing, inbox.Failed, inbox.Available,
            configuration?.LastReconciliationAt,
            configuration?.LastReconciliationStatusCode ?? "not-run", blockers.Count == 0, blockers);
    }
}
