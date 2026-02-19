using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    // Increase circuit timeout to prevent 1006 disconnections
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.DisconnectedCircuitMaxRetained = 100;
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
    options.MaxBufferedUnacknowledgedRenderBatches = 10;
})
.AddCircuitOptions(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        options.DetailedErrors = true;
    }
});

// Configure SignalR Hub options
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 128 * 1024; // 128KB
});

// Configure MudBlazor with custom theme for better button text readability
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
});

// Add custom theme provider
builder.Services.AddScoped<MudThemeProvider>();

builder.Services.AddHttpContextAccessor();

// Configure Authentication: Azure AD Multi-tenant (primary) + JWT Bearer (fallback for APIs)
var azureAdEnabled = builder.Configuration.GetValue("AzureAd:Enabled", false);
var jwtKey = builder.Configuration["Jwt:Key"] ?? "ThisIsASecretKeyForDevelopmentOnly_DoNotUseInProduction_MakeItLonger";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "CloudDentalOffice";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "CloudDentalOffice";

var authBuilder = builder.Services.AddAuthentication(options =>
{
    if (azureAdEnabled)
    {
        // Azure AD is primary authentication for Blazor Server
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    }
    else
    {
        // Fallback to JWT for development/testing
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
});

// Configure Azure AD Multi-tenant (if enabled)
if (azureAdEnabled)
{
    authBuilder.AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();

    // Handle post-authentication user provisioning
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Events ??= new OpenIdConnectEvents();
        options.Events.OnTokenValidated = async context =>
        {
            var dbContext = context.HttpContext.RequestServices.GetRequiredService<CloudDentalDbContext>();
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();

            // Extract Azure AD claims
            var azureAdObjectId = context.Principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                ?? context.Principal?.FindFirst("oid")?.Value;
            var azureAdUpn = context.Principal?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")?.Value
                ?? context.Principal?.FindFirst("preferred_username")?.Value;
            var azureAdTenantId = context.Principal?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                ?? context.Principal?.FindFirst("tid")?.Value;
            var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value ?? azureAdUpn;
            var name = context.Principal?.FindFirst(ClaimTypes.Name)?.Value ?? email;

            if (string.IsNullOrEmpty(azureAdObjectId) || string.IsNullOrEmpty(azureAdTenantId))
            {
                logger.LogWarning("Azure AD authentication succeeded but missing required claims (oid or tid)");
                context.Fail("Missing required Azure AD claims");
                return;
            }

            try
            {
                // Find or create organization for this Azure AD tenant
                var organization = await dbContext.Organizations
                    .FirstOrDefaultAsync(o => o.AzureAdTenantId == azureAdTenantId);

                if (organization == null)
                {
                    // First user from this tenant - create organization
                    logger.LogInformation("Creating new organization for Azure AD tenant: {TenantId}", azureAdTenantId);
                    
                    var domain = email?.Contains("@") == true ? email.Split('@')[1] : null;
                    
                    organization = new Organization
                    {
                        TenantId = Guid.NewGuid().ToString(),
                        AzureAdTenantId = azureAdTenantId,
                        Name = domain ?? "New Organization",
                        Domain = domain,
                        Plan = "trial",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        TrialExpiresAt = DateTime.UtcNow.AddDays(14)
                    };

                    dbContext.Organizations.Add(organization);
                    await dbContext.SaveChangesAsync();
                }

                // Find or create user
                var user = await dbContext.Users.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.AzureAdObjectId == azureAdObjectId);

                if (user == null)
                {
                    // New user - create account
                    logger.LogInformation("Creating new user for Azure AD object: {ObjectId}", azureAdObjectId);
                    
                    // First user in organization becomes admin
                    var isFirstUser = !await dbContext.Users.IgnoreQueryFilters()
                        .AnyAsync(u => u.OrganizationId == organization.Id);

                    user = new User
                    {
                        TenantId = organization.TenantId,
                        OrganizationId = organization.Id,
                        Email = email ?? $"{azureAdObjectId}@unknown.com",
                        FirstName = name?.Split(' ').FirstOrDefault() ?? "Unknown",
                        LastName = name?.Split(' ').Skip(1).FirstOrDefault() ?? "User",
                        Role = isFirstUser ? "Admin" : "Staff",
                        AzureAdObjectId = azureAdObjectId,
                        AzureAdUpn = azureAdUpn,
                        PasswordHash = null, // Azure AD users don't need password
                        CanInviteUsers = isFirstUser,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        LastLoginAt = DateTime.UtcNow
                    };

                    dbContext.Users.Add(user);
                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    // Update last login timestamp
                    user.LastLoginAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }

                // Add custom claims for application use
                var claims = new List<System.Security.Claims.Claim>
                {
                    new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new System.Security.Claims.Claim(ClaimTypes.Email, user.Email),
                    new System.Security.Claims.Claim(ClaimTypes.Role, user.Role),
                    new System.Security.Claims.Claim("TenantId", user.TenantId),
                    new System.Security.Claims.Claim("OrganizationId", user.OrganizationId?.ToString() ?? "")
                };

                var appIdentity = new ClaimsIdentity(claims);
                context.Principal?.AddIdentity(appIdentity);

                logger.LogInformation("User {Email} authenticated via Azure AD (Org: {OrgName})", 
                    user.Email, organization.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error provisioning user from Azure AD");
                context.Fail("Error provisioning user account");
            }
        };
    });
}

// Add Razor Pages (needed for Microsoft.Identity.Web.UI)
builder.Services.AddRazorPages()
    .AddMicrosoftIdentityUI();

// Add HttpClient for API calls
builder.Services.AddHttpClient();

// Tenant resolution (Blazor Server-compatible)
builder.Services.AddScoped<ITenantProvider, BlazorTenantProvider>();

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? builder.Configuration["Database:ConnectionString"]
    ?? "Server=localhost,1433;Database=CloudDentalOffice;User Id=sa;Password=YourStrong@Password123;TrustServerCertificate=True";

builder.Services.AddDbContext<CloudDentalDbContext>(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        options.UseSqlite(connectionString);
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
    else
    {
        // Production: Use PostgreSQL (DigitalOcean Managed DB)
        options.UseNpgsql(connectionString, pgOptions => 
        {
            pgOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
        });
    }
});


// Configure Azure Key Vault for secrets management (cloud-ready)
if (!builder.Environment.IsDevelopment())
{
    var keyVaultUrl = builder.Configuration["KeyVault:VaultUri"];
    if (!string.IsNullOrEmpty(keyVaultUrl))
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultUrl),
            new Azure.Identity.DefaultAzureCredential());
    }
}

// Add application services
// Patient service: configurable between monolith (DbContext) and microservice (HTTP) mode
var usePatientMicroservice = builder.Configuration.GetValue("Microservices:Patient:Enabled", false);
if (usePatientMicroservice)
{
    var gatewayUrl = builder.Configuration.GetValue<string>("ApiGateway:BaseUrl") ?? "http://localhost:5200";
    builder.Services.AddHttpClient<IPatientService, PatientServiceHttpClient>(client =>
    {
        client.BaseAddress = new Uri(gatewayUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    builder.Services.AddScoped<IPatientService, PatientServiceImpl>();
}

// Prescription service: always use microservice mode (no monolith fallback)
var prescriptionGatewayUrl = builder.Configuration.GetValue<string>("ApiGateway:BaseUrl") ?? "http://localhost:5200";
builder.Services.AddHttpClient<CloudDentalOffice.Contracts.Prescriptions.IPrescriptionService, PrescriptionServiceHttpClient>(client =>
{
    client.BaseAddress = new Uri(prescriptionGatewayUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Vision service: always use microservice mode
var visionGatewayUrl = builder.Configuration.GetValue<string>("ApiGateway:BaseUrl") ?? "http://localhost:5200";
builder.Services.AddHttpClient<CloudDentalOffice.Contracts.Vision.IVisionService, VisionServiceHttpClient>(client =>
{
    client.BaseAddress = new Uri(visionGatewayUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Remaining services still use monolith mode (migrate one at a time)
builder.Services.AddScoped<IClaimService, ClaimServiceImpl>();
builder.Services.AddScoped<IAppointmentService, AppointmentServiceImpl>();
builder.Services.AddScoped<ITreatmentPlanService, TreatmentPlanService>();
builder.Services.AddScoped<IEdiService, EdiService>();
builder.Services.AddScoped<IProviderService, ProviderServiceImpl>();
builder.Services.AddScoped<IProcedureCodeService, ProcedureCodeServiceImpl>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<IInsurancePlanService, InsurancePlanService>();
builder.Services.AddScoped<IClinicalChartService, ClinicalChartService>();

// Add EDI submission services
builder.Services.AddScoped<IEdiX12Service, EdiX12Service>();
builder.Services.AddScoped<IEdiSftpService, EdiSftpService>();
builder.Services.AddScoped<ICloudHealthOfficeApiService, CloudHealthOfficeApiService>();
builder.Services.AddScoped<IEdiSubmissionService, EdiSubmissionService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<AuthenticationStateProvider, PortalAuthStateProvider>();

var app = builder.Build();

// Ensure database is created and migrations are applied
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Applying database migrations...");
        
        // Apply migrations in all environments
        logger.LogInformation("Running database migrations...");
        dbContext.Database.Migrate();
        
        logger.LogInformation("Database setup completed successfully");
        
        // Seed initial data
        logger.LogInformation("Initializing database with seed data...");
        CloudDentalOffice.Portal.Data.DbInitializer.Initialize(dbContext);
        logger.LogInformation("Database initialization completed");
        
        // Seed claims for demo tenant
        logger.LogInformation("Seeding claims for demo tenant...");
        CloudDentalOffice.Portal.Data.DbInitializer.SeedClaims(dbContext, TenantConstants.DefaultTenantId);
        logger.LogInformation("Claims seeding completed");

        // Verify demo user exists
        var demoUserExists = dbContext.Users.IgnoreQueryFilters().Any(u => u.Email == "demo@clouddentaloffice.com");
        if (demoUserExists)
        {
            logger.LogInformation("✅ Demo user verified: demo@clouddentaloffice.com");
        }
        else
        {
            logger.LogWarning("⚠️  Demo user not found in database!");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during database initialization: {Message}", ex.Message);
        logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
        
        // Don't throw - let the app start so we can see the error in logs
        // In production, you might want to fail fast instead
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
