using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Main database context for Cloud Dental Office
/// </summary>
public class CloudDentalDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;
    private string CurrentTenantId => _tenantProvider.TenantId;

    public CloudDentalDbContext(DbContextOptions<CloudDentalDbContext> options)
        : this(options, null)
    {
    }

    public CloudDentalDbContext(
        DbContextOptions<CloudDentalDbContext> options,
        ITenantProvider? tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider ?? new DefaultTenantProvider();
    }

    // DbSets
    public DbSet<TenantRegistry> Tenants => Set<TenantRegistry>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientInsurance> PatientInsurances => Set<PatientInsurance>();
    public DbSet<InsurancePlan> InsurancePlans => Set<InsurancePlan>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<ReviewOutreach> ReviewOutreaches => Set<ReviewOutreach>();
    public DbSet<ReviewOutreachSettings> ReviewOutreachSettings => Set<ReviewOutreachSettings>();
    public DbSet<TreatmentPlan> TreatmentPlans => Set<TreatmentPlan>();
    public DbSet<PlannedProcedure> PlannedProcedures => Set<PlannedProcedure>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimProcedure> ClaimProcedures => Set<ClaimProcedure>();
    public DbSet<ProcedureCode> ProcedureCodes => Set<ProcedureCode>();
    public DbSet<Procedure> Procedures => Set<Procedure>();
    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<PatientLedgerEntry> PatientLedgerEntries => Set<PatientLedgerEntry>();
    public DbSet<PatientStatement> PatientStatements => Set<PatientStatement>();
    public DbSet<PatientStatementLine> PatientStatementLines => Set<PatientStatementLine>();
    public DbSet<PatientPayment> PatientPayments => Set<PatientPayment>();
    public DbSet<PatientPaymentAllocation> PatientPaymentAllocations => Set<PatientPaymentAllocation>();
    public DbSet<FinancialAuditEvent> FinancialAuditEvents => Set<FinancialAuditEvent>();
    public DbSet<PaymentProcessorConfiguration> PaymentProcessorConfigurations => Set<PaymentProcessorConfiguration>();
    public DbSet<PaymentProcessorEvent> PaymentProcessorEvents => Set<PaymentProcessorEvent>();
    public DbSet<PatientPaymentAttempt> PatientPaymentAttempts => Set<PatientPaymentAttempt>();
    public DbSet<PatientPortalIdentity> PatientPortalIdentities => Set<PatientPortalIdentity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureTenantEntity<Patient>(modelBuilder);
        ConfigureTenantEntity<PatientInsurance>(modelBuilder);
        ConfigureTenantEntity<InsurancePlan>(modelBuilder);
        ConfigureTenantEntity<Provider>(modelBuilder);
        ConfigureTenantEntity<Appointment>(modelBuilder);
        ConfigureTenantEntity<ReviewOutreach>(modelBuilder);
        ConfigureTenantEntity<ReviewOutreachSettings>(modelBuilder);
        ConfigureTenantEntity<TreatmentPlan>(modelBuilder);
        ConfigureTenantEntity<PlannedProcedure>(modelBuilder);
        ConfigureTenantEntity<Claim>(modelBuilder);
        ConfigureTenantEntity<ClaimProcedure>(modelBuilder);
        ConfigureTenantEntity<PatientAccount>(modelBuilder);
        ConfigureTenantEntity<PatientLedgerEntry>(modelBuilder);
        ConfigureTenantEntity<PatientStatement>(modelBuilder);
        ConfigureTenantEntity<PatientStatementLine>(modelBuilder);
        ConfigureTenantEntity<PatientPayment>(modelBuilder);
        ConfigureTenantEntity<PatientPaymentAllocation>(modelBuilder);
        ConfigureTenantEntity<FinancialAuditEvent>(modelBuilder);
        ConfigureTenantEntity<PaymentProcessorConfiguration>(modelBuilder);
        ConfigureTenantEntity<PaymentProcessorEvent>(modelBuilder);
        ConfigureTenantEntity<PatientPaymentAttempt>(modelBuilder);
        ConfigureTenantEntity<PatientPortalIdentity>(modelBuilder);

        modelBuilder.Entity<PatientPortalIdentity>(entity =>
        {
            entity.HasOne<Patient>().WithMany().HasForeignKey(x => new { x.TenantId, x.PatientId })
                .HasPrincipalKey(x => new { x.TenantId, x.PatientId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.Issuer, x.Subject }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PatientId, x.IsActive });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientAccount>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.PatientId }).IsUnique();
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientLedgerEntry>(entity =>
        {
            entity.HasAlternateKey(x => new { x.TenantId, x.LedgerEntryId });
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.PatientAccount).WithMany(x => x.LedgerEntries)
                .HasForeignKey(x => new { x.TenantId, x.PatientAccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReversalOfEntry).WithMany().HasForeignKey(x => x.ReversalOfEntryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId, x.EntryType }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ReversalOfEntryId }).IsUnique()
                .HasFilter("\"ReversalOfEntryId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.PatientAccountId, x.EffectiveDate });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientStatement>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.BalanceForward).HasPrecision(18, 2);
            entity.Property(x => x.NewCharges).HasPrecision(18, 2);
            entity.Property(x => x.InsurancePayments).HasPrecision(18, 2);
            entity.Property(x => x.Adjustments).HasPrecision(18, 2);
            entity.Property(x => x.PatientPayments).HasPrecision(18, 2);
            entity.Property(x => x.Credits).HasPrecision(18, 2);
            entity.Property(x => x.Refunds).HasPrecision(18, 2);
            entity.Property(x => x.DebitAdjustments).HasPrecision(18, 2);
            entity.Property(x => x.AmountDue).HasPrecision(18, 2);
            entity.HasAlternateKey(x => new { x.TenantId, x.StatementId });
            entity.HasOne(x => x.PatientAccount).WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PatientAccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.PatientAccountId, x.StatementDate });
            entity.HasIndex(x => new { x.TenantId, x.PatientAccountId, x.LedgerThroughDate });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientStatementLine>(entity =>
        {
            entity.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.HasOne(x => x.Statement).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.TenantId, x.StatementId })
                .HasPrincipalKey(x => new { x.TenantId, x.StatementId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientLedgerEntry>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.LedgerEntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.LedgerEntryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.StatementId, x.LedgerEntryId }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientPayment>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Method).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Processor).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasAlternateKey(x => new { x.TenantId, x.PaymentId });
            entity.HasOne(x => x.PatientAccount).WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PatientAccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientStatement>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.StatementId })
                .HasPrincipalKey(x => new { x.TenantId, x.StatementId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientLedgerEntry>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.LedgerEntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.LedgerEntryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientLedgerEntry>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ReversalLedgerEntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.LedgerEntryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.InternalPaymentReference }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Processor, x.ExternalPaymentId }).IsUnique()
                .HasFilter("\"ExternalPaymentId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.PatientAccountId, x.PaymentDate });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientPaymentAllocation>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.HasOne(x => x.Payment).WithMany(x => x.Allocations)
                .HasForeignKey(x => new { x.TenantId, x.PaymentId })
                .HasPrincipalKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientLedgerEntry>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.LedgerEntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.LedgerEntryId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.PaymentId, x.LedgerEntryId }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<FinancialAuditEvent>(entity =>
        {
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId, x.CreatedAt });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PaymentProcessorConfiguration>(entity =>
        {
            entity.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Environment).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.OnboardingStatus).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.TenantId, x.Provider }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PaymentProcessorEvent>(entity =>
        {
            entity.Property(x => x.Processor).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasOne<PatientPayment>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PaymentId })
                .HasPrincipalKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.Processor, x.ExternalEventId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PaymentId, x.CreatedAt });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PatientPaymentAttempt>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Selection).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasOne<PatientAccount>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PatientAccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientStatement>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.StatementId })
                .HasPrincipalKey(x => new { x.TenantId, x.StatementId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PatientPayment>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PaymentId })
                .HasPrincipalKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.PaymentReference }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PaymentId }).IsUnique()
                .HasFilter("\"PaymentId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.StripeCheckoutSessionId }).IsUnique()
                .HasFilter("\"StripeCheckoutSessionId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.StripePaymentIntentId }).IsUnique()
                .HasFilter("\"StripePaymentIntentId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.PatientAccountId, x.CreatedAt });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        // Patient configuration
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.HasIndex(e => e.LastName);
            entity.HasIndex(e => e.Email);
            entity.HasIndex(e => new { e.LastName, e.FirstName });
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // PatientInsurance configuration
        modelBuilder.Entity<PatientInsurance>(entity =>
        {
            entity.HasOne(pi => pi.Patient)
                .WithMany(p => p.Insurances)
                .HasForeignKey(pi => pi.PatientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(pi => pi.InsurancePlan)
                .WithMany(ip => ip.PatientInsurances)
                .HasForeignKey(pi => pi.InsurancePlanId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.MemberId);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // InsurancePlan configuration
        modelBuilder.Entity<InsurancePlan>(entity =>
        {
            entity.HasIndex(e => e.PayerId);
            entity.HasIndex(e => e.PayerName);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // Provider configuration
        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasIndex(e => e.NPI).IsUnique();
            entity.HasIndex(e => new { e.LastName, e.FirstName });
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // Appointment configuration
        // NOTE: Patient/Provider relationships removed - data in separate microservice databases
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasIndex(e => e.PatientId);
            entity.HasIndex(e => e.ProviderId);

            entity.HasIndex(e => e.AppointmentDateTime);
            entity.HasIndex(e => new { e.AppointmentDateTime, e.ProviderId });
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ReviewOutreach>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ReviewOutreachSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // TreatmentPlan configuration
        // NOTE: Patient/Provider relationships removed - data in separate microservice databases
        modelBuilder.Entity<TreatmentPlan>(entity =>
        {
            entity.HasIndex(e => e.PatientId);
            entity.HasIndex(e => e.ProviderId);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // PlannedProcedure configuration
        modelBuilder.Entity<PlannedProcedure>(entity =>
        {
            entity.HasOne(pp => pp.TreatmentPlan)
                .WithMany(tp => tp.PlannedProcedures)
                .HasForeignKey(pp => pp.TreatmentPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            // NOTE: ClaimProcedure relationship removed - claims data in separate microservice database
            entity.HasIndex(e => e.ClaimProcedureId);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // Claim configuration
        // NOTE: Patient/Provider relationships removed - data in separate microservice databases
        modelBuilder.Entity<Claim>(entity =>
        {
            entity.HasIndex(e => e.PatientId);
            entity.HasIndex(e => e.ProviderId);

            entity.HasOne(c => c.PatientInsurance)
                .WithMany()
                .HasForeignKey(c => c.PatientInsuranceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.ClaimNumber).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubmittedDate);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // ClaimProcedure configuration
        modelBuilder.Entity<ClaimProcedure>(entity =>
        {
            entity.HasOne(cp => cp.Claim)
                .WithMany(c => c.Procedures)
                .HasForeignKey(cp => cp.ClaimId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.CDTCode);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        // ProcedureCode configuration
        modelBuilder.Entity<ProcedureCode>(entity =>
        {
            entity.HasIndex(e => e.Code).IsUnique();
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.IsActive);
        });

        // Procedure configuration
        // NOTE: Patient/Provider/Appointment relationships removed - data in separate microservice databases  
        modelBuilder.Entity<Procedure>(entity =>
        {
            entity.HasIndex(e => e.PatientId);
            entity.HasIndex(e => e.ProviderId);
            entity.HasIndex(e => e.AppointmentId);

            entity.HasIndex(e => e.ServiceDate);
            entity.HasIndex(e => e.CDTCode);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
        
        ConfigureTenantEntity<Procedure>(modelBuilder);

        // ClinicalNote configuration
        // NOTE: Patient/Provider relationships removed - data in separate microservice databases
        modelBuilder.Entity<ClinicalNote>(entity =>
        {
            entity.HasIndex(e => e.PatientId);
            entity.HasIndex(e => e.ProviderId);

            entity.HasIndex(e => e.NoteDate);
            entity.HasIndex(e => e.NoteType);
            entity.HasIndex(e => e.TenantId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        ConfigureTenantEntity<ClinicalNote>(modelBuilder);

        // Seed data
        SeedData(modelBuilder);
    }

    private void SeedData(ModelBuilder modelBuilder)
    {
        // Model seed values must be deterministic or every migration rewrites them.
        var seedCreatedDate = new DateTime(2026, 2, 13, 0, 0, 0, DateTimeKind.Utc);
        // Seed common dental procedure codes
        modelBuilder.Entity<ProcedureCode>().HasData(
            // Diagnostic
            new ProcedureCode { ProcedureCodeId = 1, Code = "D0120", Description = "Periodic oral evaluation - established patient", AbbrDesc = "Periodic Exam", DefaultFee = 75.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 2, Code = "D0140", Description = "Limited oral evaluation - problem focused", AbbrDesc = "Limited Exam", DefaultFee = 65.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 3, Code = "D0150", Description = "Comprehensive oral evaluation - new or established patient", AbbrDesc = "Comp Exam", DefaultFee = 95.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 4, Code = "D0210", Description = "Intraoral - complete series of radiographic images", AbbrDesc = "FMX", DefaultFee = 125.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 5, Code = "D0220", Description = "Intraoral - periapical first radiographic image", AbbrDesc = "PA", DefaultFee = 35.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 6, Code = "D0230", Description = "Intraoral - periapical each additional radiographic image", AbbrDesc = "PA Add'l", DefaultFee = 25.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 7, Code = "D0270", Description = "Bitewing - single radiographic image", AbbrDesc = "BW Single", DefaultFee = 30.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 8, Code = "D0274", Description = "Bitewings - four radiographic images", AbbrDesc = "4 BWs", DefaultFee = 65.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 9, Code = "D0330", Description = "Panoramic radiographic image", AbbrDesc = "Pano", DefaultFee = 95.00m, Category = "Diagnostic", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Preventive
            new ProcedureCode { ProcedureCodeId = 10, Code = "D1110", Description = "Prophylaxis - adult", AbbrDesc = "Adult Prophy", DefaultFee = 95.00m, Category = "Preventive", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 11, Code = "D1120", Description = "Prophylaxis - child", AbbrDesc = "Child Prophy", DefaultFee = 75.00m, Category = "Preventive", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 12, Code = "D1206", Description = "Topical application of fluoride varnish", AbbrDesc = "Fluoride Varnish", DefaultFee = 35.00m, Category = "Preventive", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 13, Code = "D1208", Description = "Topical application of fluoride - excluding varnish", AbbrDesc = "Fluoride Treatment", DefaultFee = 30.00m, Category = "Preventive", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 14, Code = "D1351", Description = "Sealant - per tooth", AbbrDesc = "Sealant", DefaultFee = 55.00m, Category = "Preventive", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Restorative
            new ProcedureCode { ProcedureCodeId = 15, Code = "D2140", Description = "Amalgam - one surface, primary or permanent", AbbrDesc = "Amalgam 1 Surf", DefaultFee = 140.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 16, Code = "D2150", Description = "Amalgam - two surfaces, primary or permanent", AbbrDesc = "Amalgam 2 Surf", DefaultFee = 175.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 17, Code = "D2160", Description = "Amalgam - three surfaces, primary or permanent", AbbrDesc = "Amalgam 3 Surf", DefaultFee = 210.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 18, Code = "D2330", Description = "Resin-based composite - one surface, anterior", AbbrDesc = "Comp 1 Surf Ant", DefaultFee = 155.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 19, Code = "D2331", Description = "Resin-based composite - two surfaces, anterior", AbbrDesc = "Comp 2 Surf Ant", DefaultFee = 185.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 20, Code = "D2332", Description = "Resin-based composite - three surfaces, anterior", AbbrDesc = "Comp 3 Surf Ant", DefaultFee = 220.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 21, Code = "D2391", Description = "Resin-based composite - one surface, posterior", AbbrDesc = "Comp 1 Surf Post", DefaultFee = 165.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 22, Code = "D2392", Description = "Resin-based composite - two surfaces, posterior", AbbrDesc = "Comp 2 Surf Post", DefaultFee = 195.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 23, Code = "D2393", Description = "Resin-based composite - three surfaces, posterior", AbbrDesc = "Comp 3 Surf Post", DefaultFee = 235.00m, Category = "Restorative", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Endodontics
            new ProcedureCode { ProcedureCodeId = 24, Code = "D3310", Description = "Endodontic therapy, anterior tooth", AbbrDesc = "RCT Anterior", DefaultFee = 750.00m, Category = "Endodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 25, Code = "D3320", Description = "Endodontic therapy, premolar tooth", AbbrDesc = "RCT Premolar", DefaultFee = 900.00m, Category = "Endodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 26, Code = "D3330", Description = "Endodontic therapy, molar tooth", AbbrDesc = "RCT Molar", DefaultFee = 1150.00m, Category = "Endodontics", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Periodontics
            new ProcedureCode { ProcedureCodeId = 27, Code = "D4341", Description = "Periodontal scaling and root planing - four or more teeth per quadrant", AbbrDesc = "SRP per Quad", DefaultFee = 240.00m, Category = "Periodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 28, Code = "D4342", Description = "Periodontal scaling and root planing - one to three teeth per quadrant", AbbrDesc = "SRP 1-3 Teeth", DefaultFee = 140.00m, Category = "Periodontics", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Prosthodontics - Removable
            new ProcedureCode { ProcedureCodeId = 29, Code = "D5110", Description = "Complete denture - maxillary", AbbrDesc = "Upper Denture", DefaultFee = 1500.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 30, Code = "D5120", Description = "Complete denture - mandibular", AbbrDesc = "Lower Denture", DefaultFee = 1500.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 31, Code = "D5213", Description = "Partial denture - maxillary, resin base", AbbrDesc = "Upper Partial", DefaultFee = 1200.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 32, Code = "D5214", Description = "Partial denture - mandibular, resin base", AbbrDesc = "Lower Partial", DefaultFee = 1200.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Prosthodontics - Fixed
            new ProcedureCode { ProcedureCodeId = 33, Code = "D6240", Description = "Pontic - porcelain fused to high noble metal", AbbrDesc = "PFM Pontic", DefaultFee = 950.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 34, Code = "D6750", Description = "Crown - porcelain fused to high noble metal", AbbrDesc = "PFM Crown", DefaultFee = 1100.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 35, Code = "D6010", Description = "Surgical placement of endosteal implant", AbbrDesc = "Implant Placement", DefaultFee = 2000.00m, Category = "Prosthodontics", IsActive = true, CreatedDate = seedCreatedDate },
            
            // Oral Surgery
            new ProcedureCode { ProcedureCodeId = 36, Code = "D7140", Description = "Extraction, erupted tooth or exposed root", AbbrDesc = "Simple Extraction", DefaultFee = 150.00m, Category = "Oral Surgery", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 37, Code = "D7210", Description = "Extraction, erupted tooth requiring removal of bone and/or sectioning of tooth", AbbrDesc = "Surgical Extraction", DefaultFee = 250.00m, Category = "Oral Surgery", IsActive = true, CreatedDate = seedCreatedDate },
            new ProcedureCode { ProcedureCodeId = 38, Code = "D7240", Description = "Removal of impacted tooth - completely bony", AbbrDesc = "Impacted Tooth", DefaultFee = 400.00m, Category = "Oral Surgery", IsActive = true, CreatedDate = seedCreatedDate }
        );

        // Seed sample providers
        modelBuilder.Entity<Provider>().HasData(
            new Provider 
            { 
                ProviderId = 1, 
                TenantId = TenantConstants.DefaultTenantId,
                NPI = "1234567890",
                FirstName = "John", 
                LastName = "Smith", 
                Suffix = "DDS",
                Specialty = "General Dentistry",
                LicenseNumber = "D12345",
                LicenseState = "CA",
                Email = "jsmith@clouddentaloffice.com",
                Phone = "555-0101",
                IsActive = true,
                CreatedDate = seedCreatedDate
            },
            new Provider 
            { 
                ProviderId = 2, 
                TenantId = TenantConstants.DefaultTenantId,
                NPI = "2345678901",
                FirstName = "Sarah", 
                LastName = "Johnson", 
                Suffix = "DMD",
                Specialty = "Pediatric Dentistry",
                LicenseNumber = "D23456",
                LicenseState = "CA",
                Email = "sjohnson@clouddentaloffice.com",
                Phone = "555-0102",
                IsActive = true,
                CreatedDate = seedCreatedDate
            },
            new Provider 
            { 
                ProviderId = 3, 
                TenantId = TenantConstants.DefaultTenantId,
                NPI = "3456789012",
                FirstName = "Michael", 
                LastName = "Chen", 
                Suffix = "DDS",
                Specialty = "Oral Surgery",
                LicenseNumber = "D34567",
                LicenseState = "CA",
                Email = "mchen@clouddentaloffice.com",
                Phone = "555-0103",
                IsActive = true,
                CreatedDate = seedCreatedDate
            },
            new Provider 
            { 
                ProviderId = 4, 
                TenantId = TenantConstants.DefaultTenantId,
                NPI = "4567890123",
                FirstName = "Emily", 
                LastName = "Rodriguez", 
                Suffix = "DMD",
                Specialty = "Endodontics",
                LicenseNumber = "D45678",
                LicenseState = "CA",
                Email = "erodriguez@clouddentaloffice.com",
                Phone = "555-0104",
                IsActive = true,
                CreatedDate = seedCreatedDate
            }
        );
    }

    public override int SaveChanges()
    {
        EnforceImmutableLedger();
        ApplyTenantId();
        NormalizeDateTimesToUtc();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceImmutableLedger();
        ApplyTenantId();
        NormalizeDateTimesToUtc();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void EnforceImmutableLedger()
    {
        if (ChangeTracker.Entries<PatientLedgerEntry>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Posted patient ledger entries are immutable; post a reversal or adjustment instead.");
        if (ChangeTracker.Entries<FinancialAuditEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Financial audit events are immutable.");
        if (ChangeTracker.Entries<PatientStatementLine>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Statement snapshot lines are immutable.");
        var mutableStatusProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PatientStatement.Status), nameof(PatientStatement.StatusUpdatedAt),
            nameof(PatientStatement.VoidedAt), nameof(PatientStatement.VoidReasonCode),
            nameof(PatientStatement.SupersedesStatementId), nameof(PatientStatement.SupersededByStatementId)
        };
        foreach (var entry in ChangeTracker.Entries<PatientStatement>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Patient statements cannot be deleted; void or supersede them instead.");
            if (entry.State == EntityState.Modified && entry.Properties.Any(x => x.IsModified && !mutableStatusProperties.Contains(x.Metadata.Name)))
                throw new InvalidOperationException("Statement financial snapshots are immutable after creation.");
        }
        var mutableAllocationProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PatientPaymentAllocation.UnappliedAt), nameof(PatientPaymentAllocation.UnappliedBy),
            nameof(PatientPaymentAllocation.UnapplyReasonCode)
        };
        foreach (var entry in ChangeTracker.Entries<PatientPaymentAllocation>())
        {
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified &&
                entry.Properties.Any(x => x.IsModified && !mutableAllocationProperties.Contains(x.Metadata.Name)))
                throw new InvalidOperationException("Payment allocations are immutable; unapply them instead.");
        }
        var mutablePaymentProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PatientPayment.ExternalSessionId), nameof(PatientPayment.ExternalPaymentId),
            nameof(PatientPayment.Status), nameof(PatientPayment.PaymentDate),
            nameof(PatientPayment.LedgerEntryId), nameof(PatientPayment.UpdatedAt),
            nameof(PatientPayment.ReversalLedgerEntryId), nameof(PatientPayment.ReversedAt), nameof(PatientPayment.ReversedBy)
        };
        foreach (var entry in ChangeTracker.Entries<PatientPayment>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Patient payments cannot be deleted.");
            if (entry.State == EntityState.Modified && entry.Properties.Any(x => x.IsModified && !mutablePaymentProperties.Contains(x.Metadata.Name)))
                throw new InvalidOperationException("Patient payment financial identity is immutable after creation.");
        }
        var mutableEventProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PaymentProcessorEvent.Status), nameof(PaymentProcessorEvent.FailureCode),
            nameof(PaymentProcessorEvent.ProcessedAt)
        };
        foreach (var entry in ChangeTracker.Entries<PaymentProcessorEvent>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Processor idempotency events cannot be deleted.");
            if (entry.State == EntityState.Modified && entry.Properties.Any(x => x.IsModified && !mutableEventProperties.Contains(x.Metadata.Name)))
                throw new InvalidOperationException("Processor event identity is immutable after receipt.");
        }
    }

    /// <summary>
    /// PostgreSQL (Npgsql) maps <see cref="DateTime"/> to <c>timestamp with time zone</c>
    /// and rejects any value whose <see cref="DateTime.Kind"/> is not
    /// <see cref="DateTimeKind.Utc"/>, throwing "An error occurred while saving the
    /// entity changes. See the inner exception for details." A single missed
    /// conversion anywhere in the app is enough to break a save, so this acts as a
    /// last-line safety net: every DateTime being written is stamped as UTC.
    ///
    /// Values already marked UTC are left untouched (services that intentionally
    /// convert local -> UTC still win). <see cref="DateTimeKind.Local"/> values are
    /// converted to the equivalent UTC instant, preserving the moment in time.
    /// <see cref="DateTimeKind.Unspecified"/> values - what the UI date/time pickers
    /// produce - are treated as already representing UTC wall-clock time and are
    /// simply labelled UTC (no shift), matching how the app stores picker input.
    /// </summary>
    private void NormalizeDateTimesToUtc()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified)
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTime dateTime && dateTime.Kind != DateTimeKind.Utc)
                {
                    var normalized = dateTime.Kind == DateTimeKind.Local
                        ? dateTime.ToUniversalTime()
                        : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                    // DateTime.Equals compares ticks but not Kind. EF can therefore
                    // discard a CurrentValue assignment that changes only Kind.
                    // Write through the CLR property so the UTC stamp reaches the
                    // entity before a relational provider validates the value.
                    if (property.Metadata.PropertyInfo is { } propertyInfo)
                    {
                        propertyInfo.SetValue(entry.Entity, normalized);
                    }
                    else
                    {
                        property.CurrentValue = normalized;
                    }
                }
            }
        }
    }

    private void ApplyTenantId()
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = TenantConstants.DefaultTenantId;
        }

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (string.IsNullOrWhiteSpace(entry.Entity.TenantId))
                {
                    entry.Entity.TenantId = tenantId;
                }
            }
        }
    }

    private void ConfigureTenantEntity<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantEntity
    {
        modelBuilder.Entity<TEntity>()
            .Property(e => e.TenantId)
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue(TenantConstants.DefaultTenantId);

        modelBuilder.Entity<TEntity>()
            .HasIndex(e => e.TenantId);
    }
}
