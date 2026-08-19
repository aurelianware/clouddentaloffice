using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

public static class AuthConfiguration
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var key = configuration["Jwt:Key"];
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32 || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("AuthService requires a strong JWT key, issuer, and audience.");
        if (environment.IsProduction() && (configuration.GetValue("DemoAuth:Enabled", false) ||
            key.Contains("Dev-Key", StringComparison.OrdinalIgnoreCase) || key.Contains("DevelopmentOnly", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Development authentication settings are forbidden in production.");
        if (environment.IsProduction() &&
            !string.Equals(configuration["DatabaseProvider"], "PostgreSQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AuthService requires PostgreSQL in production.");
        if (environment.IsProduction() && string.IsNullOrWhiteSpace(configuration.GetConnectionString("AuthDb")))
            throw new InvalidOperationException("AuthService requires ConnectionStrings:AuthDb in production.");
    }
}

public static class AuthDatabase
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();
        if (!configuration.GetValue("DemoAuth:Enabled", false)) return;
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Demo authentication is development-only.");
        var email = configuration["DemoAuth:Email"]?.Trim();
        var password = configuration["DemoAuth:Password"];
        var tenantId = configuration["DemoAuth:TenantId"]?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || password.Length < 12 || string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Development demo identity configuration is incomplete.");
        var normalized = email.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized)) return;
        var user = new AuthUser { Email = email, NormalizedEmail = normalized,
            DisplayName = configuration["DemoAuth:DisplayName"] ?? "Demo Administrator" };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUser>>().HashPassword(user, password);
        user.Memberships.Add(new()
        {
            TenantId = tenantId, TenantName = configuration["DemoAuth:TenantName"] ?? "Demo Practice",
            Roles = configuration["DemoAuth:Roles"] ?? "Admin"
        });
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }
}
