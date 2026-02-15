using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ClaimsDbContext>(options =>
{
    var provider = builder.Configuration.GetValue("DatabaseProvider", "Sqlite");
    switch (provider)
    {
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("ClaimsDb"));
            break;
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("ClaimsDb"));
            break;
        default:
            options.UseSqlite(builder.Configuration.GetConnectionString("ClaimsDb") ?? "Data Source=claims.db");
            break;
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Claims Service", Version = "v1" }));
builder.Services.AddHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health");

app.MapGet("/api/claims", async (ClaimsDbContext db, ClaimStatus? status, int page = 1, int pageSize = 25) =>
{
    var query = db.Claims.Include(c => c.Lines).AsQueryable();
    if (status.HasValue) query = query.Where(c => c.Status == status.Value);
    var claims = await query
        .OrderByDescending(c => c.CreatedAt)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .ToListAsync();
    return Results.Ok(claims);
}).WithTags("Claims");

app.MapGet("/api/claims/{id:guid}", async (Guid id, ClaimsDbContext db) =>
{
    var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id);
    return claim is not null ? Results.Ok(claim) : Results.NotFound();
}).WithTags("Claims");

app.MapPost("/api/claims", async (CreateClaimRequest request, ClaimsDbContext db) =>
{
    var claim = new Claim
    {
        Id = Guid.NewGuid(),
        PatientId = request.PatientId,
        ProviderId = request.ProviderId,
        PayerId = request.PayerId,
        SubscriberId = request.SubscriberId,
        GroupNumber = request.GroupNumber,
        Status = ClaimStatus.Draft,
        ServiceDate = request.ServiceDate,
        TotalCharge = request.Lines.Sum(l => l.Charge),
        CreatedAt = DateTime.UtcNow,
        Lines = request.Lines.Select((l, i) => new ClaimLine
        {
            Id = Guid.NewGuid(),
            LineNumber = i + 1,
            CdtCode = l.CdtCode,
            ToothNumber = l.ToothNumber,
            Surface = l.Surface,
            Area = l.Area,
            Charge = l.Charge
        }).ToList()
    };

    db.Claims.Add(claim);
    await db.SaveChangesAsync();
    return Results.Created($"/api/claims/{claim.Id}", claim);
}).WithTags("Claims");

app.MapPost("/api/claims/{id:guid}/submit", async (Guid id, ClaimsDbContext db) =>
{
    var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id);
    if (claim is null) return Results.NotFound();
    if (claim.Status != ClaimStatus.Draft && claim.Status != ClaimStatus.Validated)
        return Results.BadRequest(new { Error = $"Cannot submit claim in {claim.Status} status" });

    // TODO: Generate 837D via EdiCommon and submit via SFTP/API
    claim.Status = ClaimStatus.Submitted;
    claim.SubmittedAt = DateTime.UtcNow;
    claim.ClaimControlNumber = $"CDO{DateTime.UtcNow:yyyyMMddHHmmss}{claim.Id.ToString()[..4]}";

    await db.SaveChangesAsync();
    return Results.Ok(new { claim.Id, claim.Status, claim.ClaimControlNumber });
}).WithTags("Claims");

app.MapGet("/api/claims/dashboard", async (ClaimsDbContext db) =>
{
    var stats = new
    {
        Draft = await db.Claims.CountAsync(c => c.Status == ClaimStatus.Draft),
        Submitted = await db.Claims.CountAsync(c => c.Status == ClaimStatus.Submitted),
        Pending = await db.Claims.CountAsync(c => c.Status == ClaimStatus.Pending),
        Paid = await db.Claims.CountAsync(c => c.Status == ClaimStatus.Paid),
        Denied = await db.Claims.CountAsync(c => c.Status == ClaimStatus.Denied),
        TotalBilled = await db.Claims.SumAsync(c => c.TotalCharge),
        TotalPaid = await db.Claims.Where(c => c.PaidAmount.HasValue).SumAsync(c => c.PaidAmount!.Value),
    };
    return Results.Ok(stats);
}).WithTags("Dashboard");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();

// ── Entities ──

public class Claim
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public string PayerId { get; set; } = string.Empty;
    public string? PayerName { get; set; }
    public string SubscriberId { get; set; } = string.Empty;
    public string? GroupNumber { get; set; }
    public ClaimStatus Status { get; set; }
    public DateOnly ServiceDate { get; set; }
    public decimal TotalCharge { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? ClaimControlNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? AdjudicatedAt { get; set; }
    public List<ClaimLine> Lines { get; set; } = [];
}

public class ClaimLine
{
    public Guid Id { get; set; }
    public Guid ClaimId { get; set; }
    public int LineNumber { get; set; }
    public string CdtCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ToothNumber { get; set; }
    public string? Surface { get; set; }
    public string? Area { get; set; }
    public decimal Charge { get; set; }
    public decimal? PaidAmount { get; set; }
}

public class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Claim>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.PatientId);
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.ClaimControlNumber);
            e.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.ClaimId);
            e.Property(c => c.TotalCharge).HasPrecision(12, 2);
            e.Property(c => c.PaidAmount).HasPrecision(12, 2);
        });
        modelBuilder.Entity<ClaimLine>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Charge).HasPrecision(10, 2);
            e.Property(l => l.PaidAmount).HasPrecision(10, 2);
            e.Property(l => l.CdtCode).HasMaxLength(10);
            e.Property(l => l.ToothNumber).HasMaxLength(5);
            e.Property(l => l.Surface).HasMaxLength(10);
        });
    }
}
