using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

public sealed class IdentityServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AuthDbContext _db = null!;
    private PasswordHasher<AuthUser> _hasher = new();
    private IdentityService _identity = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _identity = new(_db, _hasher, Issuer());
    }
    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task Correct_hashed_credentials_issue_server_authoritative_tenant_and_roles()
    {
        var user = await AddUser("staff@example.test", "CorrectPassword!", "tenant-a", "FrontDesk,Billing");
        var result = await _identity.AuthenticateAsync(user.Email, "CorrectPassword!");
        Assert.NotNull(result);
        Assert.Equal("tenant-a", result!.Response.Tenant!.Id);
        Assert.Equal(["FrontDesk", "Billing"], result.Response.Roles);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Response.AccessToken);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal("tenant-a", jwt.Claims.Single(x => x.Type == "tenant_id").Value);
        Assert.Contains(jwt.Claims, x => x.Type == ClaimTypes.Role && x.Value == "FrontDesk");
        Assert.DoesNotContain(jwt.Claims, x => x.Value == "Dentist");
        Assert.Null(result.Response.SelectionToken);
        Assert.DoesNotContain("Password", System.Text.Json.JsonSerializer.Serialize(result.Response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("staff@example.test", "wrong")]
    [InlineData("unknown@example.test", "CorrectPassword!")]
    [InlineData("", "CorrectPassword!")]
    [InlineData("staff@example.test", "")]
    [InlineData("anything", "anything")]
    public async Task Invalid_or_arbitrary_credentials_never_authenticate(string username, string password)
    {
        await AddUser("staff@example.test", "CorrectPassword!", "tenant-a", "Admin");
        Assert.Null(await _identity.AuthenticateAsync(username, password));
    }

    [Fact]
    public async Task Disabled_user_or_membership_cannot_authenticate()
    {
        var user = await AddUser("disabled@example.test", "CorrectPassword!", "tenant-a", "Admin");
        user.Enabled = false; await _db.SaveChangesAsync();
        Assert.Null(await _identity.AuthenticateAsync(user.Email, "CorrectPassword!"));
        user.Enabled = true; user.Memberships[0].Enabled = false; await _db.SaveChangesAsync();
        Assert.Null(await _identity.AuthenticateAsync(user.Email, "CorrectPassword!"));
    }

    [Fact]
    public async Task Multi_tenant_selection_allows_membership_and_denies_other_tenant()
    {
        var user = await AddUser("multi@example.test", "CorrectPassword!", "tenant-a", "Admin");
        _db.TenantMemberships.Add(new() { UserId = user.Id, TenantId = "tenant-b", TenantName = "B", Roles = "Dentist" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var login = (await _identity.AuthenticateAsync(user.Email, "CorrectPassword!"))!.Response;
        Assert.True(login.RequiresTenantSelection);
        Assert.Null(login.AccessToken);
        Assert.Equal(2, login.Tenants.Count);
        Assert.Null(await _identity.SelectTenantAsync(login.SelectionToken!, "tenant-c"));
        var selected = await _identity.SelectTenantAsync(login.SelectionToken!, "tenant-b");
        Assert.Equal("tenant-b", selected!.Tenant!.Id);
        Assert.Equal(["Dentist"], selected.Roles);
        Assert.Equal("tenant-b", new JwtSecurityTokenHandler().ReadJwtToken(selected.AccessToken)
            .Claims.Single(x => x.Type == "tenant_id").Value);
    }

    [Fact]
    public void Production_configuration_rejects_missing_key_and_demo_mode()
    {
        Assert.Throws<InvalidOperationException>(() => AuthConfiguration.Validate(new ConfigurationBuilder().Build(), new Env("Production")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "issuer", ["Jwt:Audience"] = "audience", ["DemoAuth:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => AuthConfiguration.Validate(config, new Env("Production")));
    }

    private async Task<AuthUser> AddUser(string email, string password, string tenant, string roles)
    {
        var user = new AuthUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = "Test User" };
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.Memberships.Add(new() { TenantId = tenant, TenantName = tenant, Roles = roles });
        _db.Users.Add(user); await _db.SaveChangesAsync(); return user;
    }
    private const string Key = "unit-test-key-that-is-at-least-thirty-two-bytes-long";
    private static JwtTokenIssuer Issuer() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "issuer", ["Jwt:Audience"] = "audience" }).Build(),
        Options.Create(new AuthSecurityOptions()), TimeProvider.System);
    private sealed class Env(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name; public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = ""; public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
