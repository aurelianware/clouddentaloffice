using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Client for CloudHealthOffice REST API
/// Sends generated X12 837D files to CloudHealthOffice's canonical raw-837 ingress.
/// </summary>
public interface ICloudHealthOfficeApiService
{
    Task<CloudHealthOfficeResponse> SubmitClaimAsync(Claim claim, InsurancePlan payer);
    Task<bool> TestConnectionAsync(InsurancePlan payer);
}

public class CloudHealthOfficeApiService : ICloudHealthOfficeApiService
{
    private readonly CloudDentalDbContext _context;
    private readonly IEdiX12Service _x12Service;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudHealthOfficeApiService> _logger;

    public CloudHealthOfficeApiService(
        CloudDentalDbContext context,
        IEdiX12Service x12Service,
        IHttpClientFactory httpClientFactory,
        ILogger<CloudHealthOfficeApiService> logger)
    {
        _context = context;
        _x12Service = x12Service;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CloudHealthOfficeResponse> SubmitClaimAsync(Claim claim, InsurancePlan payer)
    {
        if (string.IsNullOrEmpty(payer.ApiEndpoint))
            throw new InvalidOperationException($"Payer {payer.PayerName} has no API endpoint configured");

        if (string.IsNullOrWhiteSpace(claim.TenantId))
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} has no tenant ID");

        // Load full claim with all relationships
        var fullClaim = await _context.Claims
            .Include(c => c.Patient)
            .Include(c => c.Provider)
            .Include(c => c.PatientInsurance)
                .ThenInclude(pi => pi!.InsurancePlan)
            .Include(c => c.Procedures)
            .FirstOrDefaultAsync(c => c.ClaimId == claim.ClaimId);

        if (fullClaim == null)
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} not found");

        if (string.IsNullOrWhiteSpace(fullClaim.TenantId))
            throw new InvalidOperationException($"Claim {fullClaim.ClaimNumber} has no tenant ID");

        var x12 = await _x12Service.Generate837DTransactionAsync(fullClaim);

        _logger.LogInformation("Submitting claim {ClaimNumber} to CloudHealthOffice API: {Endpoint}",
            fullClaim.ClaimNumber, payer.ApiEndpoint);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(payer.ApiEndpoint);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Tenant-ID", fullClaim.TenantId);

            // Add authentication
            if (!string.IsNullOrEmpty(payer.ApiKeyEncrypted))
            {
                var apiKey = DecryptValue(payer.ApiKeyEncrypted);
                
                if (payer.ApiAuthType == "Bearer")
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
                else // ApiKey or default
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                }
            }

            using var form = new MultipartFormDataContent();
            using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(x12));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", $"837D_{fullClaim.ClaimNumber}.x12");

            var response = await client.PostAsync("api/v1/claims/import/raw837", form);

            if (response.IsSuccessStatusCode)
            {
                var import = await response.Content.ReadFromJsonAsync<Raw837ImportResponse>();
                var claimResult = import?.Results.FirstOrDefault();
                var result = new CloudHealthOfficeResponse
                {
                    Success = claimResult?.Success == true,
                    Message = claimResult?.Success == true
                        ? "837D accepted by CloudHealthOffice for adjudication"
                        : "CloudHealthOffice rejected the 837D claim",
                    TrackingId = claimResult?.ClaimId,
                    Errors = claimResult?.Errors
                };
                
                _logger.LogInformation("Successfully submitted claim {ClaimNumber} to CloudHealthOffice. Tracking ID: {TrackingId}",
                    fullClaim.ClaimNumber, result.TrackingId);

                return result;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("CloudHealthOffice API returned {StatusCode}: {Error}",
                    response.StatusCode, errorContent);

                throw new InvalidOperationException($"API returned {response.StatusCode}: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit claim {ClaimNumber} to CloudHealthOffice API",
                fullClaim.ClaimNumber);
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(InsurancePlan payer)
    {
        if (string.IsNullOrEmpty(payer.ApiEndpoint))
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(payer.ApiEndpoint);
            client.Timeout = TimeSpan.FromSeconds(10);

            if (!string.IsNullOrEmpty(payer.ApiKeyEncrypted))
            {
                var apiKey = DecryptValue(payer.ApiKeyEncrypted);
                if (payer.ApiAuthType == "Bearer")
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
                else
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                }
            }

            var response = await client.GetAsync("health/live");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CloudHealthOffice API connection test failed for {Endpoint}",
                payer.ApiEndpoint);
            return false;
        }
    }

    private string DecryptValue(string encryptedValue)
    {
        // TODO: Implement proper encryption/decryption
        if (string.IsNullOrEmpty(encryptedValue))
            return string.Empty;

        try
        {
            var bytes = Convert.FromBase64String(encryptedValue);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return encryptedValue;
        }
    }
}

public class Raw837ImportResponse
{
    [JsonPropertyName("succeededCount")]
    public int SucceededCount { get; set; }

    [JsonPropertyName("results")]
    public List<Raw837ClaimResponse> Results { get; set; } = [];
}

public class Raw837ClaimResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("claimId")]
    public string? ClaimId { get; set; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];
}

public class CloudHealthOfficeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("trackingId")]
    public string? TrackingId { get; set; }

    [JsonPropertyName("ediControlNumber")]
    public string? EdiControlNumber { get; set; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }
}
