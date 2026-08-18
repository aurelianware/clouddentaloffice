using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Zocdoc.IntegrationTests;

internal sealed class ZocdocSandboxClient : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new("https://api-developer-sandbox.zocdoc.com/") };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        using var auth = new HttpClient();
        using var response = await auth.PostAsJsonAsync("https://auth-api-developer-sandbox.zocdoc.com/oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = Environment.GetEnvironmentVariable("ZOCDOC_SANDBOX_CLIENT_ID"),
            client_secret = Environment.GetEnvironmentVariable("ZOCDOC_SANDBOX_CLIENT_SECRET"),
            audience = "https://api-developer-sandbox.zocdoc.com/"
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("access_token").GetString());
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}
