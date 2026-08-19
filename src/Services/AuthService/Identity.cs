using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public sealed class AuthUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(320)] public string Email { get; set; } = "";
    [MaxLength(320)] public string NormalizedEmail { get; set; } = "";
    [MaxLength(200)] public string DisplayName { get; set; } = "";
    public string? PasswordHash { get; set; }
    [MaxLength(500)] public string? ExternalIssuer { get; set; }
    [MaxLength(500)] public string? ExternalSubject { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<TenantMembership> Memberships { get; set; } = [];
}

public sealed class TenantMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AuthUser User { get; set; } = null!;
    [MaxLength(64)] public string TenantId { get; set; } = "";
    [MaxLength(200)] public string TenantName { get; set; } = "";
    [MaxLength(500)] public string Roles { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<string> RoleList() => Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<AuthUser> Users => Set<AuthUser>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthUser>().HasIndex(x => x.NormalizedEmail).IsUnique();
        modelBuilder.Entity<AuthUser>().HasIndex(x => new { x.ExternalIssuer, x.ExternalSubject }).IsUnique();
        modelBuilder.Entity<TenantMembership>().HasIndex(x => new { x.UserId, x.TenantId }).IsUnique();
        modelBuilder.Entity<TenantMembership>().HasOne(x => x.User).WithMany(x => x.Memberships)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AuthSecurityOptions
{
    [Range(15, 60)] public int AccessTokenMinutes { get; set; } = 30;
    [Range(1, 10)] public int TenantSelectionMinutes { get; set; } = 5;
}
public sealed record AuthenticatedUser(Guid Id, string DisplayName, string Email);
public sealed record AuthenticatedTenant(string Id, string Name);
public sealed record TenantChoice(string Id, string Name, IReadOnlyList<string> Roles);
public sealed record LoginResponse(string? AccessToken, DateTime? ExpiresAt, AuthenticatedUser User,
    AuthenticatedTenant? Tenant, IReadOnlyList<string> Roles, bool RequiresTenantSelection,
    string? SelectionToken, IReadOnlyList<TenantChoice> Tenants);
public sealed record AuthenticationResult(AuthUser User, LoginResponse Response);

public interface IIdentityService
{
    Task<AuthenticationResult?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<LoginResponse?> SelectTenantAsync(string selectionToken, string tenantId, CancellationToken cancellationToken = default);
}

public sealed class IdentityService(AuthDbContext db, IPasswordHasher<AuthUser> hasher, JwtTokenIssuer tokens) : IIdentityService
{
    public async Task<AuthenticationResult?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        var user = await db.Users.Include(x => x.Memberships).SingleOrDefaultAsync(
            x => x.NormalizedEmail == username.Trim().ToUpperInvariant(), cancellationToken);
        if (user is null || !user.Enabled || string.IsNullOrWhiteSpace(user.PasswordHash) ||
            hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed) return null;
        var memberships = user.Memberships.Where(x => x.Enabled).ToList();
        if (memberships.Count == 0) return null;
        return new(user, memberships.Count == 1 ? tokens.IssueAccess(user, memberships[0]) : tokens.IssueSelection(user, memberships));
    }

    public async Task<LoginResponse?> SelectTenantAsync(string selectionToken, string tenantId, CancellationToken cancellationToken = default)
    {
        var userId = tokens.ValidateSelection(selectionToken);
        if (!userId.HasValue || string.IsNullOrWhiteSpace(tenantId)) return null;
        var user = await db.Users.Include(x => x.Memberships).SingleOrDefaultAsync(x => x.Id == userId && x.Enabled, cancellationToken);
        var membership = user?.Memberships.SingleOrDefault(x => x.Enabled && x.TenantId == tenantId);
        return user is null || membership is null ? null : tokens.IssueAccess(user, membership);
    }
}

public sealed class JwtTokenIssuer(IConfiguration configuration, IOptions<AuthSecurityOptions> options, TimeProvider clock)
{
    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!));
    private string Issuer => configuration["Jwt:Issuer"]!;
    private string Audience => configuration["Jwt:Audience"]!;
    public LoginResponse IssueAccess(AuthUser user, TenantMembership membership)
    {
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(options.Value.AccessTokenMinutes);
        var claims = BaseClaims(user, now).Append(new("tenant_id", membership.TenantId))
            .Concat(membership.RoleList().Select(x => new Claim(ClaimTypes.Role, x)));
        return new(Create(claims, Audience, now, expires), expires.UtcDateTime, PublicUser(user),
            new(membership.TenantId, membership.TenantName), membership.RoleList(), false, null, []);
    }
    public LoginResponse IssueSelection(AuthUser user, IReadOnlyList<TenantMembership> memberships)
    {
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(options.Value.TenantSelectionMinutes);
        return new(null, null, PublicUser(user), null, [], true,
            Create(BaseClaims(user, now).Append(new("purpose", "tenant_selection")), Audience + ".tenant-selection", now, expires),
            memberships.Select(x => new TenantChoice(x.TenantId, x.TenantName, x.RoleList())).ToArray());
    }
    public Guid? ValidateSelection(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new()
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = Key, ValidateIssuer = true, ValidIssuer = Issuer,
                ValidateAudience = true, ValidAudience = Audience + ".tenant-selection", ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);
            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return principal.FindFirst("purpose")?.Value == "tenant_selection" && Guid.TryParse(subject, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException) { return null; }
    }
    private string Create(IEnumerable<Claim> claims, string audience, DateTimeOffset now, DateTimeOffset expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience, claims, now.UtcDateTime,
            expires.UtcDateTime, new(Key, SecurityAlgorithms.HmacSha256)));
    private static IEnumerable<Claim> BaseClaims(AuthUser user, DateTimeOffset now) =>
    [new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(JwtRegisteredClaimNames.Email, user.Email),
     new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)];
    private static AuthenticatedUser PublicUser(AuthUser user) => new(user.Id, user.DisplayName, user.Email);
}
