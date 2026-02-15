using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SchedulingDbContext>(options =>
{
    var provider = builder.Configuration.GetValue("DatabaseProvider", "Sqlite");
    switch (provider)
    {
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("SchedulingDb"));
            break;
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("SchedulingDb"));
            break;
        default:
            options.UseSqlite(builder.Configuration.GetConnectionString("SchedulingDb") ?? "Data Source=scheduling.db");
            break;
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Scheduling Service", Version = "v1" }));
builder.Services.AddHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health");

app.MapGet("/api/appointments", async (SchedulingDbContext db, DateTime? from, DateTime? to) =>
{
    var query = db.Appointments.AsQueryable();
    if (from.HasValue) query = query.Where(a => a.StartTime >= from.Value);
    if (to.HasValue) query = query.Where(a => a.StartTime <= to.Value);
    return Results.Ok(await query.OrderBy(a => a.StartTime).Take(100).ToListAsync());
}).WithTags("Appointments");

app.MapGet("/api/appointments/{id:guid}", async (Guid id, SchedulingDbContext db) =>
{
    var apt = await db.Appointments.FindAsync(id);
    return apt is not null ? Results.Ok(apt) : Results.NotFound();
}).WithTags("Appointments");

app.MapPost("/api/appointments", async (CreateAppointmentRequest request, SchedulingDbContext db) =>
{
    var apt = new Appointment
    {
        Id = Guid.NewGuid(),
        PatientId = request.PatientId,
        ProviderId = request.ProviderId,
        StartTime = request.StartTime,
        EndTime = request.EndTime,
        Status = AppointmentStatus.Scheduled,
        ProcedureCodes = request.ProcedureCodes,
        Notes = request.Notes,
        Operatory = request.Operatory,
        LocationId = request.LocationId,
        CreatedAt = DateTime.UtcNow
    };
    db.Appointments.Add(apt);
    await db.SaveChangesAsync();
    return Results.Created($"/api/appointments/{apt.Id}", apt);
}).WithTags("Appointments");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
    if (app.Environment.IsDevelopment()) await db.Database.EnsureCreatedAsync();
}

app.Run();

public class Appointment
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? ProcedureCodes { get; set; }
    public string? Notes { get; set; }
    public string? Operatory { get; set; }
    public Guid? LocationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();
}
