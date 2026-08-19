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
    Task<StripeCheckoutSessionSnapshot> CreateCheckoutSessionAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, PaymentRequest request, CancellationToken cancellationToken = default);
    Task<StripeRefundSnapshot> CreateRefundAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, PaymentRefundRequest request, string externalPaymentId,
        CancellationToken cancellationToken = default);
    Task<StripePaymentSnapshot?> GetPaymentAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, string externalPaymentId, CancellationToken cancellationToken = default);
    Task<StripeRefundSnapshot?> GetRefundAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, string externalRefundId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StripePaymentSnapshot>> ListPaymentsAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, DateTime createdAfter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StripeRefundSnapshot>> ListRefundsAsync(PaymentProcessorConfiguration configuration,
        string connectedAccountId, DateTime createdAfter, CancellationToken cancellationToken = default);
}

public sealed record StripeAccountSnapshot(string Id, bool ChargesEnabled, bool PayoutsEnabled,
    bool DetailsSubmitted, string? RequirementsStatus);
public sealed record StripeCheckoutSessionSnapshot(string Id, string? PaymentIntentId, Uri CheckoutUrl,
    DateTime ExpiresAt);
public sealed record StripePaymentSnapshot(string Id, long AmountReceived, string Currency, string Status);
public sealed record StripeRefundSnapshot(string Id, string? PaymentIntentId, string? RefundReference,
    long Amount, string Currency, string Status);

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
        var mode = processorConfiguration.Environment == PaymentProcessorEnvironment.Production ? "live" : "test";
        if (!secret.StartsWith($"sk_{mode}_", StringComparison.Ordinal) &&
            !secret.StartsWith($"rk_{mode}_", StringComparison.Ordinal))
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

    public async Task<StripeCheckoutSessionSnapshot> CreateCheckoutSessionAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, PaymentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(connectedAccountId);
        PaymentCheckoutService.ValidateReference(request.InternalPaymentReference, nameof(request.InternalPaymentReference));
        if (request.InternalPaymentReference.Length != 36 ||
            !request.InternalPaymentReference.StartsWith("pay_", StringComparison.Ordinal) ||
            !request.InternalPaymentReference[4..].All(Uri.IsHexDigit))
            throw new ArgumentException("Stripe Checkout requires a server-generated opaque payment reference.",
                nameof(request.InternalPaymentReference));
        if (request.Amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(request.Amount));
        ValidateCheckoutUrl(request.SuccessUrl, config.Environment, nameof(request.SuccessUrl));
        ValidateCheckoutUrl(request.CancelUrl, config.Environment, nameof(request.CancelUrl));
        var cents = checked((long)(request.Amount.Amount * 100m));
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = request.SuccessUrl!,
            ["cancel_url"] = request.CancelUrl!,
            ["line_items[0][price_data][currency]"] = request.Amount.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = cents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = "Account payment",
            ["line_items[0][quantity]"] = "1",
            ["metadata[payment_reference]"] = request.InternalPaymentReference,
            ["payment_intent_data[metadata][payment_reference]"] = request.InternalPaymentReference
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.GetSecret(config));
        message.Headers.Add("Stripe-Account", connectedAccountId);
        message.Headers.Add("Idempotency-Key", request.InternalPaymentReference);
        message.Headers.Add("Stripe-Version", configuration["Stripe:Checkout:ApiVersion"] ?? "2026-07-29.dahlia");
        message.Content = new FormUrlEncodedContent(fields);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new StripeConnectException($"Stripe Checkout request failed with HTTP {(int)response.StatusCode}.");
        var dto = await ReadAsync<StripeCheckoutSessionDto>(response, cancellationToken);
        if (!dto.Id.StartsWith("cs_", StringComparison.Ordinal) ||
            !Uri.TryCreate(dto.Url, UriKind.Absolute, out var checkoutUrl) || checkoutUrl.Scheme != Uri.UriSchemeHttps)
            throw new StripeConnectException("Stripe returned an invalid Checkout Session.");
        return new(dto.Id, dto.PaymentIntentId, checkoutUrl, ParseTimestamp(dto.ExpiresAt));
    }

    public async Task<StripeRefundSnapshot> CreateRefundAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, PaymentRefundRequest request, string externalPaymentId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(connectedAccountId);
        ValidatePaymentIntentId(externalPaymentId);
        PaymentCheckoutService.ValidateReference(request.InternalRefundReference, nameof(request.InternalRefundReference));
        var fields = new Dictionary<string, string>
        {
            ["payment_intent"] = externalPaymentId,
            ["amount"] = StripeCurrency.ToMinorUnits(request.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["metadata[refund_reference]"] = request.InternalRefundReference
        };
        if (request.Reason is "duplicate" or "fraudulent" or "requested_by_customer") fields["reason"] = request.Reason;
        using var response = await SendV1Async(config, connectedAccountId, HttpMethod.Post, "/v1/refunds", fields,
            request.InternalRefundReference, cancellationToken);
        return MapRefund(await ReadAsync<StripeRefundDto>(response, cancellationToken));
    }

    public async Task<StripePaymentSnapshot?> GetPaymentAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, string externalPaymentId, CancellationToken cancellationToken = default)
    {
        ValidatePaymentIntentId(externalPaymentId);
        using var response = await SendV1Async(config, connectedAccountId, HttpMethod.Get,
            $"/v1/payment_intents/{Uri.EscapeDataString(externalPaymentId)}", null, null, cancellationToken, false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        EnsureStripeSuccess(response, "payment retrieval");
        return MapPayment(await ReadAsync<StripePaymentIntentDto>(response, cancellationToken));
    }

    public async Task<StripeRefundSnapshot?> GetRefundAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, string externalRefundId, CancellationToken cancellationToken = default)
    {
        ValidateRefundId(externalRefundId);
        using var response = await SendV1Async(config, connectedAccountId, HttpMethod.Get,
            $"/v1/refunds/{Uri.EscapeDataString(externalRefundId)}", null, null, cancellationToken, false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        EnsureStripeSuccess(response, "refund retrieval");
        return MapRefund(await ReadAsync<StripeRefundDto>(response, cancellationToken));
    }

    public async Task<IReadOnlyList<StripePaymentSnapshot>> ListPaymentsAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, DateTime createdAfter, CancellationToken cancellationToken = default)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(createdAfter, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var values = new List<StripePaymentSnapshot>();
        string? startingAfter = null;
        do
        {
            var cursor = startingAfter is null ? string.Empty : $"&starting_after={Uri.EscapeDataString(startingAfter)}";
            using var response = await SendV1Async(config, connectedAccountId, HttpMethod.Get,
                $"/v1/payment_intents?limit=100&created[gte]={seconds}{cursor}", null, null, cancellationToken);
            var page = await ReadAsync<StripeListDto<StripePaymentIntentDto>>(response, cancellationToken);
            values.AddRange(page.Data.Select(MapPayment));
            startingAfter = NextCursor(page, "PaymentIntent");
        } while (startingAfter is not null);
        return values;
    }

    public async Task<IReadOnlyList<StripeRefundSnapshot>> ListRefundsAsync(PaymentProcessorConfiguration config,
        string connectedAccountId, DateTime createdAfter, CancellationToken cancellationToken = default)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(createdAfter, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var values = new List<StripeRefundSnapshot>();
        string? startingAfter = null;
        do
        {
            var cursor = startingAfter is null ? string.Empty : $"&starting_after={Uri.EscapeDataString(startingAfter)}";
            using var response = await SendV1Async(config, connectedAccountId, HttpMethod.Get,
                $"/v1/refunds?limit=100&created[gte]={seconds}{cursor}", null, null, cancellationToken);
            var page = await ReadAsync<StripeListDto<StripeRefundDto>>(response, cancellationToken);
            values.AddRange(page.Data.Select(MapRefund));
            startingAfter = NextCursor(page, "refund");
        } while (startingAfter is not null);
        return values;
    }

    private static string? NextCursor<T>(StripeListDto<T> page, string resource) where T : IStripeListItem
    {
        if (!page.HasMore) return null;
        var cursor = page.Data.LastOrDefault()?.Id;
        return !string.IsNullOrWhiteSpace(cursor) ? cursor :
            throw new StripeConnectException($"Stripe returned an invalid paginated {resource} response.");
    }

    private async Task<HttpResponseMessage> SendV1Async(PaymentProcessorConfiguration config, string accountId,
        HttpMethod method, string path, Dictionary<string, string>? fields, string? idempotencyKey,
        CancellationToken cancellationToken, bool requireSuccess = true)
    {
        ValidateAccountId(accountId);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.GetSecret(config));
        request.Headers.Add("Stripe-Account", accountId);
        request.Headers.Add("Stripe-Version", configuration["Stripe:Payments:ApiVersion"] ?? "2026-07-29.dahlia");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (requireSuccess) EnsureStripeSuccess(response, "payment operation");
        return response;
    }

    private static void EnsureStripeSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new StripeConnectException($"Stripe {operation} failed with HTTP {(int)response.StatusCode}.");
    }

    private static StripePaymentSnapshot MapPayment(StripePaymentIntentDto value) =>
        new(value.Id, value.AmountReceived, value.Currency.ToUpperInvariant(), value.Status);
    private static StripeRefundSnapshot MapRefund(StripeRefundDto value) => new(value.Id, value.PaymentIntentId,
        value.Metadata?.RefundReference, value.Amount, value.Currency.ToUpperInvariant(), value.Status);

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
    private static void ValidatePaymentIntentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.StartsWith("pi_", StringComparison.Ordinal))
            throw new ArgumentException("A valid Stripe PaymentIntent ID is required.");
    }
    private static void ValidateRefundId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.StartsWith("re_", StringComparison.Ordinal))
            throw new ArgumentException("A valid Stripe refund ID is required.");
    }
    private static void ValidateRedirect(Uri value, PaymentProcessorEnvironment environment)
    {
        if (!value.IsAbsoluteUri || (environment == PaymentProcessorEnvironment.Production && value.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Stripe production onboarding redirect URLs must use HTTPS.");
    }
    private static void ValidateCheckoutUrl(string? value, PaymentProcessorEnvironment environment, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Replace("{CHECKOUT_SESSION_ID}", "session", StringComparison.Ordinal),
                UriKind.Absolute, out var uri) ||
            (environment == PaymentProcessorEnvironment.Production && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Stripe Checkout redirect URLs must be safe absolute application URLs.", parameter);
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
internal sealed record StripeCheckoutSessionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntentId,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_at")] JsonElement ExpiresAt);
internal interface IStripeListItem { string Id { get; } }
internal sealed record StripePaymentIntentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("amount_received")] long AmountReceived,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status) : IStripeListItem;
internal sealed record StripeRefundDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntentId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("metadata")] StripeRefundMetadataDto? Metadata) : IStripeListItem;
internal sealed record StripeRefundMetadataDto(
    [property: JsonPropertyName("refund_reference")] string? RefundReference);
internal sealed record StripeListDto<T>([property: JsonPropertyName("data")] List<T> Data,
    [property: JsonPropertyName("has_more")] bool HasMore);

internal static class StripeCurrency
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BHD", "JOD", "KWD", "OMR", "TND" };
    public static long ToMinorUnits(Money value)
    {
        var multiplier = ZeroDecimal.Contains(value.Currency) ? 1m : ThreeDecimal.Contains(value.Currency) ? 1_000m : 100m;
        return decimal.ToInt64(decimal.Round(value.Amount * multiplier, 0, MidpointRounding.AwayFromZero));
    }
}

// Patient checkout/refund support is intentionally separate from Connect onboarding in this PR.
public sealed class StripePaymentProcessor(IStripeApiClient api) : IPaymentProcessor
{
    public PaymentProcessorProvider Provider => PaymentProcessorProvider.Stripe;
    public async Task<PaymentSession> CreateSessionAsync(PaymentProcessorConfiguration configuration, PaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.Enabled || configuration.OnboardingStatus != PaymentProcessorOnboardingStatus.Enabled ||
            !configuration.ChargesEnabled || !configuration.PayoutsEnabled ||
            string.IsNullOrWhiteSpace(configuration.ConnectedMerchantReference))
            throw new PaymentProcessorUnavailableException("The practice Stripe account is not ready to accept payments.");
        var session = await api.CreateCheckoutSessionAsync(configuration, configuration.ConnectedMerchantReference,
            request, cancellationToken);
        return new(request.InternalPaymentReference, session.Id, session.PaymentIntentId, session.CheckoutUrl, null,
            session.ExpiresAt, PaymentStatus.Pending);
    }
    public async Task<PaymentRefundResult> RefundAsync(PaymentProcessorConfiguration configuration, PaymentRefundRequest request,
        string externalPaymentId, CancellationToken cancellationToken = default)
    {
        if (!configuration.Enabled || configuration.OnboardingStatus != PaymentProcessorOnboardingStatus.Enabled ||
            string.IsNullOrWhiteSpace(configuration.ConnectedMerchantReference))
            throw new PaymentProcessorUnavailableException("The practice Stripe account is not ready to issue refunds.");
        var result = await api.CreateRefundAsync(configuration, configuration.ConnectedMerchantReference,
            request, externalPaymentId, cancellationToken);
        var status = result.Status == "failed" ? PaymentStatus.Failed : PaymentStatus.Pending;
        return new(request.InternalRefundReference, result.Id, status);
    }
}
