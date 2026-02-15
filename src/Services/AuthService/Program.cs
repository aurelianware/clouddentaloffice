using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Auth Service", Version = "v1" }));
builder.Services.AddHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health");

app.MapPost("/api/auth/login", (LoginRequest request, IConfiguration config) =>
{
    // TODO: Validate credentials against user store
    // This is a placeholder — replace with proper identity provider integration
    if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        return Results.BadRequest(new { Error = "Username and password required" });

    var jwtKey = config["Jwt:Key"] ?? "CloudDentalOffice-Dev-Key-Replace-In-Production-Min32Chars!!";
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var claims = new[]
    {
        new System.Security.Claims.Claim(ClaimTypes.Name, request.Username),
        new System.Security.Claims.Claim(ClaimTypes.Role, "Dentist"),
        new System.Security.Claims.Claim("tenant_id", request.TenantId ?? "default"),
    };

    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"] ?? "CloudDentalOffice",
        audience: config["Jwt:Audience"] ?? "CloudDentalOffice",
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: credentials
    );

    return Results.Ok(new
    {
        Token = new JwtSecurityTokenHandler().WriteToken(token),
        ExpiresAt = token.ValidTo,
        Username = request.Username
    });
}).WithTags("Authentication");

app.MapPost("/api/auth/refresh", () =>
{
    // TODO: Refresh token endpoint
    return Results.Ok(new { Message = "Not yet implemented" });
}).WithTags("Authentication");

app.Run();

public record LoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? TenantId { get; init; }
}
