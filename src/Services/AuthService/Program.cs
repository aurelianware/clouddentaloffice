using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Auth Service", Version = "v1" }));
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AuthDbContext>(options =>
{
    if (builder.Configuration.GetValue("DatabaseProvider", "Sqlite") == "PostgreSQL")
        options.UseNpgsql(builder.Configuration.GetConnectionString("AuthDb"));
    else options.UseSqlite(builder.Configuration.GetConnectionString("AuthDb") ?? "Data Source=auth.db");
});
builder.Services.AddScoped<Microsoft.AspNetCore.Identity.IPasswordHasher<AuthUser>, Microsoft.AspNetCore.Identity.PasswordHasher<AuthUser>>();
builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddSingleton<JwtTokenIssuer>();
builder.Services.AddOptions<AuthSecurityOptions>().Bind(builder.Configuration.GetSection("Auth"))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var value in builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? [])
        if (IPAddress.TryParse(value, out var address)) options.KnownProxies.Add(address);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
AuthConfiguration.Validate(app.Configuration, app.Environment);
await AuthDatabase.InitializeAsync(app.Services, app.Configuration, app.Environment);
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseForwardedHeaders();
app.UseRateLimiter();
app.MapHealthChecks("/health");

app.MapPost("/api/auth/login", async (LoginRequest request, IIdentityService identity,
    ILogger<Program> logger, HttpContext http, CancellationToken cancellationToken) =>
{
    var result = await identity.AuthenticateAsync(request.Username, request.Password, cancellationToken);
    if (result is null)
    {
        logger.LogWarning("AuthenticationFailed correlation {CorrelationId}.", http.TraceIdentifier);
        return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
    }
    logger.LogInformation("AuthenticationSucceeded user {UserId} correlation {CorrelationId}.", result.User.Id, http.TraceIdentifier);
    return Results.Ok(result.Response);
}).RequireRateLimiting("login").WithTags("Authentication");

app.MapPost("/api/auth/select-tenant", async (TenantSelectionRequest request, IIdentityService identity,
    ILogger<Program> logger, HttpContext http, CancellationToken cancellationToken) =>
{
    var response = await identity.SelectTenantAsync(request.SelectionToken, request.TenantId, cancellationToken);
    if (response is null)
    {
        logger.LogWarning("TenantSelectionDenied correlation {CorrelationId}.", http.TraceIdentifier);
        return Results.Json(new { error = "Tenant selection is not authorized." }, statusCode: 403);
    }
    logger.LogInformation("TokenIssued tenant {TenantId} correlation {CorrelationId}.", response.Tenant!.Id, http.TraceIdentifier);
    return Results.Ok(response);
}).RequireRateLimiting("login").WithTags("Authentication");

app.Run();
public partial class Program { }
public sealed record LoginRequest(string Username, string Password);
public sealed record TenantSelectionRequest(string SelectionToken, string TenantId);
