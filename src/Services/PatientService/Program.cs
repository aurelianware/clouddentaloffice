using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Patients;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");

// ── Patient Endpoints ──

app.MapGet("/api/patients", async (PatientDbContext db, string? tenantId) =>
{
    var query = db.Patients
        .Include(p => p.Insurances)
            .ThenInclude(pi => pi.InsurancePlan)
        .Where(p => p.Status != "Archived");

    if (!string.IsNullOrEmpty(tenantId))
        query = query.Where(p => p.TenantId == tenantId);

    var patients = await query
        .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
        .Select(p => p.ToDto())
        .ToListAsync();
    return Results.Ok(patients);
})
.WithName("GetPatients")
.WithTags("Patients");

app.MapGet("/api/patients/{id:int}", async (int id, PatientDbContext db) =>
{
    var patient = await db.Patients
        .Include(p => p.Insurances)
            .ThenInclude(pi => pi.InsurancePlan)
        .FirstOrDefaultAsync(p => p.PatientId == id);
    return patient is not null ? Results.Ok(patient.ToDto()) : Results.NotFound();
})
.WithName("GetPatient")
.WithTags("Patients");

app.MapPost("/api/patients", async (CreatePatientRequest request, PatientDbContext db, string? tenantId) =>
{
    var patient = new PatientEntity
    {
        TenantId = tenantId ?? "default",
        FirstName = request.FirstName,
        LastName = request.LastName,
        MiddleName = request.MiddleName,
        PreferredName = request.PreferredName,
        DateOfBirth = NormalizeToUtc(request.DateOfBirth),
        Gender = request.Gender,
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

app.MapPut("/api/patients/{id:int}", async (int id, UpdatePatientRequest request, PatientDbContext db) =>
{
    var patient = await db.Patients.FindAsync(id);
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
        .Include(p => p.Insurances).ThenInclude(pi => pi.InsurancePlan)
        .FirstAsync(p => p.PatientId == id);
    return Results.Ok(updated.ToDto());
})
.WithName("UpdatePatient")
.WithTags("Patients");

app.MapDelete("/api/patients/{id:int}", async (int id, PatientDbContext db) =>
{
    var patient = await db.Patients.FindAsync(id);
    if (patient is null) return Results.NotFound();

    // Soft delete
    patient.Status = "Archived";
    patient.ModifiedDate = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeletePatient")
.WithTags("Patients");

app.MapGet("/api/patients/search", async (string q, PatientDbContext db, string? tenantId) =>
{
    var query = db.Patients
        .Include(p => p.Insurances).ThenInclude(pi => pi.InsurancePlan)
        .Where(p => p.Status != "Archived")
        .Where(p => p.LastName.Contains(q) || p.FirstName.Contains(q) ||
                    (p.Email != null && p.Email.Contains(q)));

    if (!string.IsNullOrEmpty(tenantId))
        query = query.Where(p => p.TenantId == tenantId);

    var patients = await query.Take(50).Select(p => p.ToDto()).ToListAsync();
    return Results.Ok(patients);
})
.WithName("SearchPatients")
.WithTags("Patients");

// ── Insurance Plan Endpoints ──

app.MapGet("/api/insurance-plans", async (PatientDbContext db, string? tenantId) =>
{
    var query = db.InsurancePlans.AsQueryable();
    if (!string.IsNullOrEmpty(tenantId))
        query = query.Where(p => p.TenantId == tenantId);

    var plans = await query.OrderBy(p => p.PayerName)
        .Select(p => p.ToDto()).ToListAsync();
    return Results.Ok(plans);
})
.WithName("GetInsurancePlans")
.WithTags("Insurance");

app.MapGet("/api/insurance-plans/{id:int}", async (int id, PatientDbContext db) =>
{
    var plan = await db.InsurancePlans.FindAsync(id);
    return plan is not null ? Results.Ok(plan.ToDto()) : Results.NotFound();
})
.WithName("GetInsurancePlan")
.WithTags("Insurance");

app.MapPost("/api/insurance-plans", async (CreateInsurancePlanRequest request, PatientDbContext db, string? tenantId) =>
{
    var plan = new InsurancePlanEntity
    {
        TenantId = tenantId ?? "default",
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

app.MapPost("/api/patients/{patientId:int}/insurances", async (
    int patientId, CreatePatientInsuranceRequest request, PatientDbContext db) =>
{
    var patient = await db.Patients.FindAsync(patientId);
    if (patient is null) return Results.NotFound("Patient not found");

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

// Auto-migrate in development
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
    if (app.Environment.IsDevelopment())
        await db.Database.EnsureCreatedAsync();
}

app.Run();
