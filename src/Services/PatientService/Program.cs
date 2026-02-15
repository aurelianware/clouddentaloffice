using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Patients;

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

app.MapGet("/api/patients", async (PatientDbContext db, int page = 1, int pageSize = 25) =>
{
    var patients = await db.Patients
        .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .Select(p => p.ToDto())
        .ToListAsync();
    return Results.Ok(patients);
})
.WithName("GetPatients")
.WithTags("Patients");

app.MapGet("/api/patients/{id:guid}", async (Guid id, PatientDbContext db) =>
{
    var patient = await db.Patients.FindAsync(id);
    return patient is not null ? Results.Ok(patient.ToDto()) : Results.NotFound();
})
.WithName("GetPatient")
.WithTags("Patients");

app.MapPost("/api/patients", async (CreatePatientRequest request, PatientDbContext db) =>
{
    var patient = new Patient
    {
        Id = Guid.NewGuid(),
        FirstName = request.FirstName,
        LastName = request.LastName,
        DateOfBirth = request.DateOfBirth,
        Email = request.Email,
        Phone = request.Phone,
        AddressLine1 = request.Address?.Line1,
        AddressLine2 = request.Address?.Line2,
        City = request.Address?.City,
        State = request.Address?.State,
        ZipCode = request.Address?.ZipCode,
        SubscriberId = request.SubscriberId,
        PayerId = request.PayerId,
        GroupNumber = request.GroupNumber,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Patients.Add(patient);
    await db.SaveChangesAsync();
    return Results.Created($"/api/patients/{patient.Id}", patient.ToDto());
})
.WithName("CreatePatient")
.WithTags("Patients");

app.MapPut("/api/patients/{id:guid}", async (Guid id, UpdatePatientRequest request, PatientDbContext db) =>
{
    var patient = await db.Patients.FindAsync(id);
    if (patient is null) return Results.NotFound();

    if (request.FirstName is not null) patient.FirstName = request.FirstName;
    if (request.LastName is not null) patient.LastName = request.LastName;
    if (request.Email is not null) patient.Email = request.Email;
    if (request.Phone is not null) patient.Phone = request.Phone;
    if (request.SubscriberId is not null) patient.SubscriberId = request.SubscriberId;
    if (request.PayerId is not null) patient.PayerId = request.PayerId;
    if (request.GroupNumber is not null) patient.GroupNumber = request.GroupNumber;
    if (request.Address is not null)
    {
        patient.AddressLine1 = request.Address.Line1;
        patient.AddressLine2 = request.Address.Line2;
        patient.City = request.Address.City;
        patient.State = request.Address.State;
        patient.ZipCode = request.Address.ZipCode;
    }
    patient.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(patient.ToDto());
})
.WithName("UpdatePatient")
.WithTags("Patients");

app.MapGet("/api/patients/search", async (string q, PatientDbContext db) =>
{
    var patients = await db.Patients
        .Where(p => p.LastName.Contains(q) || p.FirstName.Contains(q) ||
                    (p.SubscriberId != null && p.SubscriberId.Contains(q)))
        .Take(50)
        .Select(p => p.ToDto())
        .ToListAsync();
    return Results.Ok(patients);
})
.WithName("SearchPatients")
.WithTags("Patients");

// Auto-migrate in development
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
    if (app.Environment.IsDevelopment())
        await db.Database.EnsureCreatedAsync();
}

app.Run();

// ── Entity & DbContext ──

public class Patient
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? SubscriberId { get; set; }
    public string? PayerId { get; set; }
    public string? GroupNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public PatientDto ToDto() => new()
    {
        Id = Id,
        FirstName = FirstName,
        LastName = LastName,
        DateOfBirth = DateOfBirth,
        Email = Email,
        Phone = Phone,
        Address = AddressLine1 is not null ? new AddressDto
        {
            Line1 = AddressLine1,
            Line2 = AddressLine2,
            City = City ?? string.Empty,
            State = State ?? string.Empty,
            ZipCode = ZipCode ?? string.Empty
        } : null,
        SubscriberId = SubscriberId,
        PayerId = PayerId,
        GroupNumber = GroupNumber,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Patient>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.LastName, p.FirstName });
            e.HasIndex(p => p.SubscriberId);
            e.HasIndex(p => p.PayerId);
            e.Property(p => p.FirstName).HasMaxLength(100).IsRequired();
            e.Property(p => p.LastName).HasMaxLength(100).IsRequired();
            e.Property(p => p.Email).HasMaxLength(255);
            e.Property(p => p.Phone).HasMaxLength(20);
            e.Property(p => p.SubscriberId).HasMaxLength(50);
            e.Property(p => p.PayerId).HasMaxLength(20);
            e.Property(p => p.GroupNumber).HasMaxLength(50);
            e.Property(p => p.State).HasMaxLength(2);
            e.Property(p => p.ZipCode).HasMaxLength(10);
        });
    }
}
