using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public sealed record StripeConnectStatus(string? ConnectedAccountId, PaymentProcessorOnboardingStatus Status,
    bool ChargesEnabled, bool PayoutsEnabled, bool DetailsSubmitted, bool Enabled, PaymentProcessorEnvironment Environment);
public sealed record StripeOnboardingLink(Uri Url, DateTime ExpiresAt);

public interface IStripeConnectService
{
    Task<StripeOnboardingLink> CreateOnboardingLinkAsync(string tenantId, string adminEmail, Uri refreshUrl,
        Uri returnUrl, CancellationToken cancellationToken = default);
    Task<StripeOnboardingLink> RefreshOnboardingLinkAsync(string tenantId, Uri refreshUrl, Uri returnUrl,
        CancellationToken cancellationToken = default);
    Task<StripeConnectStatus> RefreshStatusAsync(string tenantId, CancellationToken cancellationToken = default);
    Task DisableAsync(string tenantId, CancellationToken cancellationToken = default);
}

public interface IStripeApiClient
{
    Task<StripeAccountSnapshot> CreateConnectedAccountAsync(PaymentProcessorConfiguration configuration,
        string contactEmail, CancellationToken cancellationToken = default);
    Task<StripeAccountSnapshot> GetConnectedAccountAsync(PaymentProcessorConfiguration configuration,
        string accountId, CancellationToken cancellationToken = default);
    Task<StripeOnboardingLink> CreateAccountLinkAsync(PaymentProcessorConfiguration configuration,
        string accountId, Uri refreshUrl, Uri returnUrl, CancellationToken cancellationToken = default);
}

public sealed record StripeAccountSnapshot(string Id, bool ChargesEnabled, bool PayoutsEnabled,
    bool DetailsSubmitted, string? RequirementsStatus);

public interface IStripeCredentialProvider
{
    string GetSecret(PaymentProcessorConfiguration configuration);
}

public sealed class ConfigurationStripeCredentialProvider(IConfiguration configuration) : IStripeCredentialProvider
{
    public string GetSecret(PaymentProcessorConfiguration processorConfiguration)
    {
        if (string.IsNullOrWhiteSpace(processorConfiguration.CredentialReference))
            throw new PaymentProcessorUnavailableException("Stripe credential reference is not configured.");
        var secret = configuration[processorConfiguration.CredentialReference];
        if (string.IsNullOrWhiteSpace(secret))
            throw new PaymentProcessorUnavailableException("Stripe credentials are unavailable from the configured secret provider.");
        var expectedPrefix = processorConfiguration.Environment == PaymentProcessorEnvironment.Production ? "sk_live_" : "sk_test_";
        if (!secret.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new PaymentProcessorUnavailableException("Stripe credentials do not match the configured environment.");
        return secret;
    }
}

public sealed class StripeApiClient(HttpClient httpClient, IStripeCredentialProvider credentials,
    IConfiguration configuration) : IStripeApiClient
{
    private const string DefaultAccountsCreateVersion = "2026-07-29.preview";
    private const string DefaultAccountsReadVersion = "2026-07-29.dahlia";
    private const string DefaultAccountLinksVersion = "2026-07-29.dahlia";
    private static readonly string[] Includes = ["configuration.merchant", "identity", "requirements"];

    public async Task<StripeAccountSnapshot> CreateConnectedAccountAsync(PaymentProcessorConfiguration config,
        string contactEmail, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            contact_email = contactEmail,
            dashboard = "full",
            configuration = new { merchant = new { capabilities = new { card_payments = new { requested = true } } } },
            defaults = new
            {
                currency = "usd",
                responsibilities = new { fees_collector = "stripe", losses_collector = "stripe" },
                locales = new[] { "en-US" }
            },
            include = Includes
        };
        using var response = await SendAsync(config, HttpMethod.Post, "/v2/core/accounts", body,
            configuration["Stripe:Connect:AccountsCreateApiVersion"] ?? DefaultAccountsCreateVersion, cancellationToken);
        return Map(await ReadAsync<StripeAccountDto>(response, cancellationToken));
    }

    public async Task<StripeAccountSnapshot> GetConnectedAccountAsync(PaymentProcessorConfiguration config,
        string accountId, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        var include = string.Join('&', Includes.Select(x => $"include[]={Uri.EscapeDataString(x)}"));
        using var response = await SendAsync(config, HttpMethod.Get,
            $"/v2/core/accounts/{Uri.EscapeDataString(accountId)}?{include}", null,
            configuration["Stripe:Connect:AccountsReadApiVersion"] ?? DefaultAccountsReadVersion, cancellationToken);
        return Map(await ReadAsync<StripeAccountDto>(response, cancellationToken));
    }

    public async Task<StripeOnboardingLink> CreateAccountLinkAsync(PaymentProcessorConfiguration config,
        string accountId, Uri refreshUrl, Uri returnUrl, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        ValidateRedirect(refreshUrl, config.Environment); ValidateRedirect(returnUrl, config.Environment);
        var body = new
        {
            account = accountId,
            use_case = new
            {
                type = "account_onboarding",
                account_onboarding = new
                {
                    configurations = new[] { "merchant" },
                    refresh_url = refreshUrl.AbsoluteUri,
                    return_url = returnUrl.AbsoluteUri
                }
            }
        };
        using var response = await SendAsync(config, HttpMethod.Post, "/v2/core/account_links", body,
            configuration["Stripe:Connect:AccountLinksApiVersion"] ?? DefaultAccountLinksVersion, cancellationToken);
        var dto = await ReadAsync<StripeAccountLinkDto>(response, cancellationToken);
        if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new StripeConnectException("Stripe returned an invalid onboarding URL.");
        return new(url, ParseTimestamp(dto.ExpiresAt));
    }

    private async Task<HttpResponseMessage> SendAsync(PaymentProcessorConfiguration config, HttpMethod method,
        string path, object? body, string apiVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.GetSecret(config));
        request.Headers.Add("Stripe-Version", apiVersion);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new StripeConnectException($"Stripe Connect request failed with HTTP {(int)response.StatusCode}.");
        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
        ?? throw new StripeConnectException("Stripe returned an empty response.");
    private static StripeAccountSnapshot Map(StripeAccountDto account)
    {
        var card = account.Configuration?.Merchant?.Capabilities?.CardPayments?.Status == "active";
        var payouts = account.Configuration?.Merchant?.Capabilities?.StripeBalancePayouts?.Status == "active";
        var status = account.Requirements?.Summary?.MinimumDeadline?.Status;
        var submitted = account.Requirements?.Entries is { Count: 0 } || status is null or "satisfied";
        return new(account.Id, card, payouts, submitted, status);
    }
    private static void ValidateAccountId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.StartsWith("acct_", StringComparison.Ordinal))
            throw new ArgumentException("A valid Stripe connected-account ID is required.");
    }
    private static void ValidateRedirect(Uri value, PaymentProcessorEnvironment environment)
    {
        if (!value.IsAbsoluteUri || (environment == PaymentProcessorEnvironment.Production && value.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Stripe production onboarding redirect URLs must use HTTPS.");
    }
    private static DateTime ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp.UtcDateTime;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { }
        }
        throw new StripeConnectException("Stripe returned an invalid onboarding expiration.");
    }
}

public sealed class StripeConnectService(CloudDentalDbContext db, IStripeApiClient api, ITenantProvider tenantProvider,
    TimeProvider clock) : IStripeConnectService
{
    public async Task<StripeOnboardingLink> CreateOnboardingLinkAsync(string tenantId, string adminEmail,
        Uri refreshUrl, Uri returnUrl, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        if (string.IsNullOrWhiteSpace(adminEmail) || !adminEmail.Contains('@'))
            throw new ArgumentException("An authenticated administrator email is required.", nameof(adminEmail));
        var config = await Configuration(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(config.ConnectedMerchantReference))
        {
            var account = await api.CreateConnectedAccountAsync(config, adminEmail.Trim(), cancellationToken);
            Apply(config, account); config.CreatedAt = config.CreatedAt == default ? clock.GetUtcNow().UtcDateTime : config.CreatedAt;
            await db.SaveChangesAsync(cancellationToken);
        }
        return await api.CreateAccountLinkAsync(config, config.ConnectedMerchantReference!, refreshUrl, returnUrl, cancellationToken);
    }

    public async Task<StripeOnboardingLink> RefreshOnboardingLinkAsync(string tenantId, Uri refreshUrl, Uri returnUrl,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId); var config = await Configuration(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(config.ConnectedMerchantReference))
            throw new InvalidOperationException("The practice has no Stripe connected account.");
        return await api.CreateAccountLinkAsync(config, config.ConnectedMerchantReference, refreshUrl, returnUrl, cancellationToken);
    }

    public async Task<StripeConnectStatus> RefreshStatusAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId); var config = await Configuration(tenantId, cancellationToken, requireEnabled: false);
        if (!config.Enabled) return Status(config);
        if (string.IsNullOrWhiteSpace(config.ConnectedMerchantReference)) return Status(config);
        Apply(config, await api.GetConnectedAccountAsync(config, config.ConnectedMerchantReference, cancellationToken));
        await db.SaveChangesAsync(cancellationToken); return Status(config);
    }

    public async Task DisableAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId); var config = await Configuration(tenantId, cancellationToken);
        config.Enabled = false; config.OnboardingStatus = PaymentProcessorOnboardingStatus.Disabled;
        config.UpdatedAt = clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<PaymentProcessorConfiguration> Configuration(string tenantId, CancellationToken cancellationToken,
        bool requireEnabled = true)
    {
        var value = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Provider == PaymentProcessorProvider.Stripe, cancellationToken)
            ?? throw new PaymentProcessorUnavailableException("Stripe payment configuration is not configured for the tenant.");
        if (requireEnabled && !value.Enabled)
            throw new PaymentProcessorUnavailableException("Stripe payment configuration is disabled.");
        return value;
    }
    private void Apply(PaymentProcessorConfiguration config, StripeAccountSnapshot account)
    {
        config.ConnectedMerchantReference = account.Id; config.ChargesEnabled = account.ChargesEnabled;
        config.PayoutsEnabled = account.PayoutsEnabled; config.DetailsSubmitted = account.DetailsSubmitted;
        config.LastStatusCode = account.RequirementsStatus;
        config.OnboardingStatus = account.ChargesEnabled && account.PayoutsEnabled
            ? PaymentProcessorOnboardingStatus.Enabled
            : account.DetailsSubmitted ? PaymentProcessorOnboardingStatus.Restricted : PaymentProcessorOnboardingStatus.Pending;
        config.UpdatedAt = clock.GetUtcNow().UtcDateTime;
    }
    private static StripeConnectStatus Status(PaymentProcessorConfiguration x) => new(x.ConnectedMerchantReference,
        x.OnboardingStatus, x.ChargesEnabled, x.PayoutsEnabled, x.DetailsSubmitted, x.Enabled, x.Environment);
    private void EnsureTenant(string tenantId) => PaymentTenantGuard.Ensure(tenantProvider, tenantId);
}

public sealed class StripeConnectException(string message) : InvalidOperationException(message);

internal sealed record StripeAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("configuration")] StripeConfigurationDto? Configuration,
    [property: JsonPropertyName("requirements")] StripeRequirementsDto? Requirements);
internal sealed record StripeConfigurationDto([property: JsonPropertyName("merchant")] StripeMerchantDto? Merchant);
internal sealed record StripeMerchantDto([property: JsonPropertyName("capabilities")] StripeCapabilitiesDto? Capabilities);
internal sealed record StripeCapabilitiesDto(
    [property: JsonPropertyName("card_payments")] StripeCapabilityDto? CardPayments,
    [property: JsonPropertyName("stripe_balance")] StripeBalanceCapabilitiesDto? StripeBalance)
{
    [JsonIgnore] public StripeCapabilityDto? StripeBalancePayouts => StripeBalance?.Payouts;
}
internal sealed record StripeBalanceCapabilitiesDto(
    [property: JsonPropertyName("payouts")] StripeCapabilityDto? Payouts);
internal sealed record StripeCapabilityDto([property: JsonPropertyName("status")] string? Status);
internal sealed record StripeRequirementsDto(
    [property: JsonPropertyName("entries")] List<JsonElement>? Entries,
    [property: JsonPropertyName("summary")] StripeRequirementsSummaryDto? Summary);
internal sealed record StripeRequirementsSummaryDto(
    [property: JsonPropertyName("minimum_deadline")] StripeDeadlineDto? MinimumDeadline);
internal sealed record StripeDeadlineDto([property: JsonPropertyName("status")] string? Status);
internal sealed record StripeAccountLinkDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_at")] JsonElement ExpiresAt);

// Patient checkout/refund support is intentionally separate from Connect onboarding in this PR.
public sealed class StripePaymentProcessor : IPaymentProcessor
{
    public PaymentProcessorProvider Provider => PaymentProcessorProvider.Stripe;
    public Task<PaymentSession> CreateSessionAsync(PaymentProcessorConfiguration configuration, PaymentRequest request,
        CancellationToken cancellationToken = default) => throw new PaymentProcessorUnavailableException(
        "Stripe patient checkout is not enabled; Connect onboarding alone does not activate payment collection.");
    public Task<PaymentRefundResult> RefundAsync(PaymentProcessorConfiguration configuration, PaymentRefundRequest request,
        string externalPaymentId, CancellationToken cancellationToken = default) => throw new PaymentProcessorUnavailableException(
        "Stripe patient refunds are not enabled; Connect onboarding alone does not activate payment collection.");
}
