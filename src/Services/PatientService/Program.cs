using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using CloudDentalOffice.Contracts.Patients;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

// ── Helpers ──

static DateTime NormalizeToUtc(DateTime dateTime) => dateTime.Kind switch
{
    DateTimeKind.Local => dateTime.ToUniversalTime(),
    DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Local).ToUniversalTime(),
    _ => dateTime
};

// ── Application ──

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<PatientDbContext>(options =>
{
    var provider = builder.Configuration.GetValue("DatabaseProvider", "Sqlite");
    switch (provider)
    {
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("PatientDb"));
            break;
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("PatientDb"));
            break;
        default:
            options.UseSqlite(builder.Configuration.GetConnectionString("PatientDb") ?? "Data Source=patient.db");
            break;
    }
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Patient Service", Version = "v1" }));
builder.Services.AddHealthChecks();

// Patient and insurance-plan APIs accept the same staff bearer tokens as the
// portal; the tenant comes only from the token's tenant_id claim. Outside
// Development a missing or weak Jwt:Key fails startup. In Development a missing
// key becomes a per-process random key, so every request stays unauthorized.
var configuredJwtKey = builder.Configuration["Jwt:Key"];
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(configuredJwtKey) || Encoding.UTF8.GetByteCount(configuredJwtKey) < 32))
    throw new InvalidOperationException(
        "PatientService requires Jwt:Key (at least 32 bytes) outside Development. Supply a secret-backed Jwt__Key.");
var signingKey = string.IsNullOrWhiteSpace(configuredJwtKey)
    ? RandomNumberGenerator.GetBytes(32)
    : Encoding.UTF8.GetBytes(configuredJwtKey);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(signingKey),
        ValidateIssuer = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Issuer"]),
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Audience"]),
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy(PatientTenant.Policy, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => PatientTenant.IsStaff(context.User))));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

// Every tenant-facing route requires a staff token carrying tenant_id. Records
// belonging to another tenant are reported as 404 so their existence is not revealed.
var patientsApi = app.MapGroup("/api/patients").RequireAuthorization(PatientTenant.Policy);
var insurancePlansApi = app.MapGroup("/api/insurance-plans").RequireAuthorization(PatientTenant.Policy);

// ── Patient Endpoints ──

patientsApi.MapGet("", async (PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patients = await db.Patients
        .WithTenantInsurances(tenantId)
        .Where(p => p.TenantId == tenantId && p.Status != "Archived")
        .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
        .Select(p => p.ToDto())
        .ToListAsync();
    return Results.Ok(patients);
})
.WithName("GetPatients")
.WithTags("Patients");

patientsApi.MapGet("/{id:int}", async (int id, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patient = await db.Patients
        .WithTenantInsurances(tenantId)
        .FirstOrDefaultAsync(p => p.PatientId == id && p.TenantId == tenantId);
    return patient is not null ? Results.Ok(patient.ToDto()) : Results.NotFound();
})
.WithName("GetPatient")
.WithTags("Patients");

patientsApi.MapPost("", async (CreatePatientRequest request, PatientDbContext db, ClaimsPrincipal user) =>
{
    var patient = new PatientEntity
    {
        TenantId = PatientTenant.Of(user)!,
        FirstName = request.FirstName,
        LastName = request.LastName,
        MiddleName = request.MiddleName,
        PreferredName = request.PreferredName,
        DateOfBirth = NormalizeToUtc(request.DateOfBirth),
        Gender = request.Gender,
        SSN = request.SSN,
        Email = request.Email,
        PrimaryPhone = request.PrimaryPhone,
        SecondaryPhone = request.SecondaryPhone,
        Address1 = request.Address1,
        Address2 = request.Address2,
        City = request.City,
        State = request.State,
        ZipCode = request.ZipCode,
        Status = "Active",
        CreatedDate = DateTime.UtcNow,
    };

    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Created($"/api/patients/{patient.PatientId}", patient.ToDto());
})
.WithName("CreatePatient")
.WithTags("Patients");

app.MapPost("/api/internal/patients/match-or-create", async (
    MatchOrCreateExternalPatientRequest request, PatientDbContext db, IConfiguration configuration,
    HttpContext http, string tenantId) =>
{
    if (!InternalPatientApiAuth.IsAuthorized(http, configuration, tenantId)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(request.FirstName) ||
        string.IsNullOrWhiteSpace(request.LastName) || request.DateOfBirth == default)
        return Results.BadRequest();

    if (int.TryParse(request.DeveloperPatientId, out var knownId))
    {
        var known = await db.Patients.SingleOrDefaultAsync(x =>
            x.PatientId == knownId && x.TenantId == tenantId && x.Status != "Archived");
        if (known is not null) return Results.Ok(new MatchOrCreateExternalPatientResult(known.PatientId, false));
    }

    var dob = request.DateOfBirth.ToDateTime(TimeOnly.MinValue);
    var firstName = request.FirstName.Trim().ToLower();
    var lastName = request.LastName.Trim().ToLower();
    var candidates = await db.Patients.Where(x => x.TenantId == tenantId && x.Status != "Archived" &&
        x.FirstName.ToLower() == firstName && x.LastName.ToLower() == lastName && x.DateOfBirth.Date == dob.Date)
        .ToListAsync();
    if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(request.Email))
        candidates = candidates.Where(x => string.Equals(x.Email, request.Email, StringComparison.OrdinalIgnoreCase)).ToList();
    if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(request.Phone))
        candidates = candidates.Where(x => x.PrimaryPhone == request.Phone).ToList();
    if (candidates.Count == 1)
        return Results.Ok(new MatchOrCreateExternalPatientResult(candidates[0].PatientId, false));
    if (candidates.Count > 1) return Results.Conflict();

    var patient = new PatientEntity
    {
        TenantId = tenantId, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(),
        DateOfBirth = DateTime.SpecifyKind(dob, DateTimeKind.Utc), Gender = request.Gender ?? "U",
        Email = request.Email, PrimaryPhone = request.Phone, Status = "Active", CreatedDate = DateTime.UtcNow
    };
    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Ok(new MatchOrCreateExternalPatientResult(patient.PatientId, true));
}).WithTags("Patients");

patientsApi.MapPut("/{id:int}", async (int id, UpdatePatientRequest request, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patient = await db.Patients.FirstOrDefaultAsync(p => p.PatientId == id && p.TenantId == tenantId);
    if (patient is null) return Results.NotFound();

    if (request.FirstName is not null) patient.FirstName = request.FirstName;
    if (request.LastName is not null) patient.LastName = request.LastName;
    if (request.MiddleName is not null) patient.MiddleName = request.MiddleName;
    if (request.PreferredName is not null) patient.PreferredName = request.PreferredName;
    if (request.DateOfBirth.HasValue) patient.DateOfBirth = NormalizeToUtc(request.DateOfBirth.Value);
    if (request.Gender is not null) patient.Gender = request.Gender;
    if (request.Email is not null) patient.Email = request.Email;
    if (request.PrimaryPhone is not null) patient.PrimaryPhone = request.PrimaryPhone;
    if (request.SecondaryPhone is not null) patient.SecondaryPhone = request.SecondaryPhone;
    if (request.Address1 is not null) patient.Address1 = request.Address1;
    if (request.Address2 is not null) patient.Address2 = request.Address2;
    if (request.City is not null) patient.City = request.City;
    if (request.State is not null) patient.State = request.State;
    if (request.ZipCode is not null) patient.ZipCode = request.ZipCode;
    if (request.Status is not null) patient.Status = request.Status;
    patient.ModifiedDate = DateTime.UtcNow;

    await db.SaveChangesAsync();

    // Reload with insurance
    var updated = await db.Patients
        .WithTenantInsurances(tenantId)
        .FirstAsync(p => p.PatientId == id && p.TenantId == tenantId);
    return Results.Ok(updated.ToDto());
})
.WithName("UpdatePatient")
.WithTags("Patients");

patientsApi.MapDelete("/{id:int}", async (int id, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patient = await db.Patients.FirstOrDefaultAsync(p => p.PatientId == id && p.TenantId == tenantId);
    if (patient is null) return Results.NotFound();

    // Soft delete
    patient.Status = "Archived";
    patient.ModifiedDate = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeletePatient")
.WithTags("Patients");

patientsApi.MapGet("/search", async (string q, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patients = await db.Patients
        .WithTenantInsurances(tenantId)
        .Where(p => p.TenantId == tenantId && p.Status != "Archived")
        .Where(p => p.LastName.Contains(q) || p.FirstName.Contains(q) ||
                    (p.Email != null && p.Email.Contains(q)))
        .Take(50).Select(p => p.ToDto()).ToListAsync();
    return Results.Ok(patients);
})
.WithName("SearchPatients")
.WithTags("Patients");

// ── Insurance Plan Endpoints ──

insurancePlansApi.MapGet("", async (PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var plans = await db.InsurancePlans.Where(p => p.TenantId == tenantId).OrderBy(p => p.PayerName)
        .Select(p => p.ToDto()).ToListAsync();
    return Results.Ok(plans);
})
.WithName("GetInsurancePlans")
.WithTags("Insurance");

insurancePlansApi.MapGet("/{id:int}", async (int id, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var plan = await db.InsurancePlans.FirstOrDefaultAsync(p => p.InsurancePlanId == id && p.TenantId == tenantId);
    return plan is not null ? Results.Ok(plan.ToDto()) : Results.NotFound();
})
.WithName("GetInsurancePlan")
.WithTags("Insurance");

insurancePlansApi.MapPost("", async (CreateInsurancePlanRequest request, PatientDbContext db, ClaimsPrincipal user) =>
{
    var plan = new InsurancePlanEntity
    {
        TenantId = PatientTenant.Of(user)!,
        PayerId = request.PayerId,
        PayerName = request.PayerName,
        PlanName = request.PlanName,
        PlanType = request.PlanType,
        Phone = request.Phone,
        Address1 = request.Address1,
        Address2 = request.Address2,
        City = request.City,
        State = request.State,
        ZipCode = request.ZipCode,
        EdiPayerId = request.EdiPayerId,
        EdiEnabled = request.EdiEnabled,
        EdiSubmissionType = request.EdiSubmissionType,
        IsActive = true,
        CreatedDate = DateTime.UtcNow,
    };

    db.InsurancePlans.Add(plan);
    await db.SaveChangesAsync();
    return Results.Created($"/api/insurance-plans/{plan.InsurancePlanId}", plan.ToDto());
})
.WithName("CreateInsurancePlan")
.WithTags("Insurance");

// ── Patient Insurance Endpoints ──

patientsApi.MapPost("/{patientId:int}/insurances", async (
    int patientId, CreatePatientInsuranceRequest request, PatientDbContext db, ClaimsPrincipal user) =>
{
    var tenantId = PatientTenant.Of(user);
    var patient = await db.Patients.FirstOrDefaultAsync(p => p.PatientId == patientId && p.TenantId == tenantId);
    if (patient is null) return Results.NotFound("Patient not found");
    if (!await db.InsurancePlans.AnyAsync(p => p.InsurancePlanId == request.InsurancePlanId && p.TenantId == tenantId))
        return Results.NotFound("Insurance plan not found");

    var insurance = new PatientInsuranceEntity
    {
        TenantId = patient.TenantId,
        PatientId = patientId,
        InsurancePlanId = request.InsurancePlanId,
        MemberId = request.MemberId,
        GroupNumber = request.GroupNumber,
        SequenceNumber = request.SequenceNumber,
        EffectiveDate = request.EffectiveDate,
        TerminationDate = request.TerminationDate,
        IsActive = true,
        RelationshipToSubscriber = request.RelationshipToSubscriber,
        SubscriberFirstName = request.SubscriberFirstName,
        SubscriberLastName = request.SubscriberLastName,
        SubscriberDateOfBirth = request.SubscriberDateOfBirth,
        CreatedDate = DateTime.UtcNow,
    };

    db.PatientInsurances.Add(insurance);
    await db.SaveChangesAsync();

    // Reload with plan
    var saved = await db.PatientInsurances
        .Include(pi => pi.InsurancePlan)
        .FirstAsync(pi => pi.PatientInsuranceId == insurance.PatientInsuranceId);
    return Results.Created(
        $"/api/patients/{patientId}/insurances/{saved.PatientInsuranceId}",
        saved.ToDto());
})
.WithName("CreatePatientInsurance")
.WithTags("Insurance");

// Auto-migrate on startup (all environments)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();

// Exposes the entry point to WebApplicationFactory in PatientService.Tests.
public partial class Program { }

// ── Entities ──

[Table("Patients")]
public class PatientEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int PatientId { get; set; }

    [Required, MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? MiddleName { get; set; }

    [MaxLength(20)]
    public string? PreferredName { get; set; }

    [Required]
    public DateTime DateOfBirth { get; set; }

    [Required, MaxLength(1)]
    public string Gender { get; set; } = "U";

    [MaxLength(11)]
    public string? SSN { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(20)]
    public string? PrimaryPhone { get; set; }

    [MaxLength(20)]
    public string? SecondaryPhone { get; set; }

    [MaxLength(255)]
    public string? Address1 { get; set; }

    [MaxLength(255)]
    public string? Address2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(2)]
    public string? State { get; set; }

    [MaxLength(10)]
    public string? ZipCode { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = "Active";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    [MaxLength(100)]
    public string? ModifiedBy { get; set; }

    // Navigation
    public virtual ICollection<PatientInsuranceEntity> Insurances { get; set; } = new List<PatientInsuranceEntity>();

    public PatientDto ToDto() => new()
    {
        PatientId = PatientId,
        FirstName = FirstName,
        LastName = LastName,
        MiddleName = MiddleName,
        PreferredName = PreferredName,
        DateOfBirth = DateOfBirth,
        Gender = Gender,
        SSN = SSN,
        Email = Email,
        PrimaryPhone = PrimaryPhone,
        SecondaryPhone = SecondaryPhone,
        Address1 = Address1,
        Address2 = Address2,
        City = City,
        State = State,
        ZipCode = ZipCode,
        Status = Status,
        CreatedDate = CreatedDate,
        ModifiedDate = ModifiedDate,
        Insurances = Insurances.Select(i => i.ToDto()).ToList(),
    };
}

[Table("PatientInsurances")]
public class PatientInsuranceEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int PatientInsuranceId { get; set; }

    [Required, MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public int PatientId { get; set; }

    [Required]
    public int InsurancePlanId { get; set; }

    [Required, MaxLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? GroupNumber { get; set; }

    [Required]
    public int SequenceNumber { get; set; }

    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    [Required]
    public bool IsActive { get; set; } = true;

    [MaxLength(20)]
    public string? RelationshipToSubscriber { get; set; }

    [MaxLength(100)]
    public string? SubscriberFirstName { get; set; }

    [MaxLength(100)]
    public string? SubscriberLastName { get; set; }

    public DateTime? SubscriberDateOfBirth { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public virtual PatientEntity Patient { get; set; } = null!;
    public virtual InsurancePlanEntity InsurancePlan { get; set; } = null!;

    public PatientInsuranceDto ToDto() => new()
    {
        PatientInsuranceId = PatientInsuranceId,
        PatientId = PatientId,
        InsurancePlanId = InsurancePlanId,
        MemberId = MemberId,
        GroupNumber = GroupNumber,
        SequenceNumber = SequenceNumber,
        EffectiveDate = EffectiveDate,
        TerminationDate = TerminationDate,
        IsActive = IsActive,
        RelationshipToSubscriber = RelationshipToSubscriber,
        SubscriberFirstName = SubscriberFirstName,
        SubscriberLastName = SubscriberLastName,
        SubscriberDateOfBirth = SubscriberDateOfBirth,
        InsurancePlan = InsurancePlan?.ToDto(),
    };
}

[Table("InsurancePlans")]
public class InsurancePlanEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int InsurancePlanId { get; set; }

    [Required, MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    public string PayerId { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string PayerName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? PlanName { get; set; }

    [MaxLength(50)]
    public string? PlanType { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Address1 { get; set; }

    [MaxLength(255)]
    public string? Address2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(2)]
    public string? State { get; set; }

    [MaxLength(10)]
    public string? ZipCode { get; set; }

    [MaxLength(50)]
    public string? EdiPayerId { get; set; }

    public bool EdiEnabled { get; set; }

    [MaxLength(20)]
    public string? EdiSubmissionType { get; set; }

    [Required]
    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }

    public virtual ICollection<PatientInsuranceEntity> PatientInsurances { get; set; } = new List<PatientInsuranceEntity>();

    public InsurancePlanDto ToDto() => new()
    {
        InsurancePlanId = InsurancePlanId,
        PayerId = PayerId,
        PayerName = PayerName,
        PlanName = PlanName,
        PlanType = PlanType,
        Phone = Phone,
        Address1 = Address1,
        Address2 = Address2,
        City = City,
        State = State,
        ZipCode = ZipCode,
        EdiPayerId = EdiPayerId,
        EdiEnabled = EdiEnabled,
        EdiSubmissionType = EdiSubmissionType,
        IsActive = IsActive,
    };
}

public static class PatientTenant
{
    public const string Policy = "PatientTenant";

    // Portal-issued staff tokens (TokenService, SchedulingTenantAuthorizationHandler) carry tenant_id.
    public static string? Of(ClaimsPrincipal user) => user.FindFirstValue("tenant_id");

    // Staff roles are open-ended (Admin, Staff, Dentist, FrontDesk, ...), but the
    // Portal also signs tokens for patient-portal users with the same tenant_id.
    // Require a tenant and at least one role, and keep Patient principals out.
    public static bool IsStaff(ClaimsPrincipal user) =>
        !string.IsNullOrWhiteSpace(Of(user)) &&
        user.HasClaim(claim => claim.Type == ClaimTypes.Role && !string.IsNullOrWhiteSpace(claim.Value)) &&
        !user.IsInRole("Patient");
}

public static class PatientQueries
{
    // Loads only insurance rows, and plans, that belong to the caller's tenant, so
    // a legacy cross-tenant association never discloses another tenant's plan.
    public static IQueryable<PatientEntity> WithTenantInsurances(this IQueryable<PatientEntity> patients, string? tenantId) =>
        patients
            .Include(p => p.Insurances.Where(pi => pi.TenantId == tenantId && pi.InsurancePlan.TenantId == tenantId))
            .ThenInclude(pi => pi.InsurancePlan);
}

public static class InternalPatientApiAuth
{
    public static bool IsAuthorized(HttpContext http, IConfiguration configuration, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        var provided = http.Request.Headers["X-CDO-Service-Key"].ToString();
        if (string.IsNullOrWhiteSpace(provided)) return false;
        var client = configuration.GetSection("InternalApi:Clients").GetChildren()
            .FirstOrDefault(x => string.Equals(x["TenantId"], tenantId, StringComparison.Ordinal));
        var expected = client?["ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

// ── DbContext ──

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<PatientEntity> Patients => Set<PatientEntity>();
    public DbSet<PatientInsuranceEntity> PatientInsurances => Set<PatientInsuranceEntity>();
    public DbSet<InsurancePlanEntity> InsurancePlans => Set<InsurancePlanEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PatientEntity>(e =>
        {
            e.HasIndex(p => new { p.LastName, p.FirstName });
            e.HasIndex(p => p.Email);
            e.HasIndex(p => p.TenantId);
        });

        modelBuilder.Entity<PatientInsuranceEntity>(e =>
        {
            e.HasOne(pi => pi.Patient)
                .WithMany(p => p.Insurances)
                .HasForeignKey(pi => pi.PatientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(pi => pi.InsurancePlan)
                .WithMany(ip => ip.PatientInsurances)
                .HasForeignKey(pi => pi.InsurancePlanId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(pi => pi.MemberId);
            e.HasIndex(pi => pi.TenantId);
        });

        modelBuilder.Entity<InsurancePlanEntity>(e =>
        {
            e.HasIndex(p => p.PayerId);
            e.HasIndex(p => p.TenantId);
        });
    }
}
