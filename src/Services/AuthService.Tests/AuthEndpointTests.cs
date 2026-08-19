using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

public sealed class AuthEndpointTests
{
    [Fact]
    public async Task Login_response_is_generic_for_unknown_user_and_wrong_password()
    {
        using var factory = new AuthFactory();
        await factory.SeedAsync();
        using var unknownClient = factory.CreateClient();
        using var wrongClient = factory.CreateClient();
        var unknown = await unknownClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("unknown@example.test", "wrong"));
        var wrong = await wrongClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("endpoint@example.test", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_endpoint_rate_limits_repeated_attempts()
    {
        using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 11; attempt++)
            response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("none@example.test", "wrong"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
    }
}

public sealed class AuthFactory : WebApplicationFactory<Program>
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-auth-{Guid.NewGuid():N}.db");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DatabaseProvider", "Sqlite");
        builder.UseSetting("ConnectionStrings:AuthDb", $"Data Source={_database}");
        builder.UseSetting("Jwt:Key", "endpoint-test-key-at-least-thirty-two-characters-long");
        builder.UseSetting("Jwt:Issuer", "test"); builder.UseSetting("Jwt:Audience", "test");
        builder.UseSetting("DemoAuth:Enabled", "false");
    }
    public async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        if (db.Users.Any(x => x.NormalizedEmail == "ENDPOINT@EXAMPLE.TEST")) return;
        var user = new AuthUser { Email = "endpoint@example.test", NormalizedEmail = "ENDPOINT@EXAMPLE.TEST", DisplayName = "Endpoint" };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUser>>().HashPassword(user, "CorrectPassword!");
        user.Memberships.Add(new() { TenantId = "tenant-a", TenantName = "A", Roles = "Admin" });
        db.Users.Add(user); await db.SaveChangesAsync();
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && File.Exists(_database)) File.Delete(_database); }
}
