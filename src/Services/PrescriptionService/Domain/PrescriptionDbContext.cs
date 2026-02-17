// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;

namespace PrescriptionService.Domain;

public class PrescriptionDbContext : DbContext
{
    public PrescriptionDbContext(DbContextOptions<PrescriptionDbContext> options)
        : base(options) { }

    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PatientAllergy> PatientAllergies => Set<PatientAllergy>();
    public DbSet<Prescriber> Prescribers => Set<Prescriber>();
    public DbSet<PrescriptionAuditEntry> AuditEntries => Set<PrescriptionAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Prescription>(entity =>
        {
            entity.HasMany(p => p.AuditTrail)
                  .WithOne(a => a.Prescription)
                  .HasForeignKey(a => a.PrescriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(p => p.Quantity)
                  .HasPrecision(10, 2);
        });

        // Audit entries are append-only — no updates or deletes allowed at the app layer
        modelBuilder.Entity<PrescriptionAuditEntry>(entity =>
        {
            entity.Property(a => a.Timestamp)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }
}
