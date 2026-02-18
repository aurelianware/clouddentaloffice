// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;

namespace VisionService.Domain;

public class VisionDbContext : DbContext
{
    public VisionDbContext(DbContextOptions<VisionDbContext> options) : base(options) { }

    public DbSet<VisionDevice> Devices => Set<VisionDevice>();
    public DbSet<VisionEvent> Events => Set<VisionEvent>();
    public DbSet<CabinetAccessLog> CabinetAccessLogs => Set<CabinetAccessLog>();
    public DbSet<InsuranceCardScan> InsuranceCardScans => Set<InsuranceCardScan>();
    public DbSet<ConsentRecording> ConsentRecordings => Set<ConsentRecording>();
    public DbSet<ClinicalNoteDraft> ClinicalNoteDrafts => Set<ClinicalNoteDraft>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── VisionDevice ──
        modelBuilder.Entity<VisionDevice>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.TenantId);
            e.HasIndex(d => new { d.TenantId, d.Location });
            e.HasIndex(d => d.MacAddress).IsUnique().HasFilter("\"MacAddress\" IS NOT NULL");
            e.Property(d => d.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Location).HasConversion<string>().HasMaxLength(30);
        });

        // ── VisionEvent ──
        modelBuilder.Entity<VisionEvent>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasIndex(v => new { v.TenantId, v.Timestamp });
            e.HasIndex(v => new { v.DeviceId, v.Timestamp });
            e.HasIndex(v => v.AlertSeverity).HasFilter("\"AlertSeverity\" IS NOT NULL");
            e.HasIndex(v => v.PatientId).HasFilter("\"PatientId\" IS NOT NULL");
            e.HasIndex(v => v.AppointmentId).HasFilter("\"AppointmentId\" IS NOT NULL");
            e.Property(v => v.EventType).HasConversion<string>().HasMaxLength(30);
            e.Property(v => v.AlertSeverity).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.AlertType).HasConversion<string>().HasMaxLength(50);

            e.HasOne(v => v.Device)
                .WithMany(d => d.Events)
                .HasForeignKey(v => v.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── CabinetAccessLog ──
        modelBuilder.Entity<CabinetAccessLog>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.TenantId, c.Timestamp });
            e.HasIndex(c => new { c.DeviceId, c.Timestamp });
            e.HasIndex(c => c.ProviderId).HasFilter("\"ProviderId\" IS NOT NULL");
            e.HasIndex(c => c.Severity);
            e.Property(c => c.Severity).HasConversion<string>().HasMaxLength(20);

            e.HasOne(c => c.Device)
                .WithMany(d => d.CabinetAccessLogs)
                .HasForeignKey(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── InsuranceCardScan ──
        modelBuilder.Entity<InsuranceCardScan>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.TenantId, s.Timestamp });
            e.HasIndex(s => s.MatchedPatientId).HasFilter("\"MatchedPatientId\" IS NOT NULL");
        });

        // ── ConsentRecording ──
        modelBuilder.Entity<ConsentRecording>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.TenantId, c.PatientId });
            e.HasIndex(c => c.AppointmentId).HasFilter("\"AppointmentId\" IS NOT NULL");
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        });

        // ── ClinicalNoteDraft ──
        modelBuilder.Entity<ClinicalNoteDraft>(e =>
        {
            e.HasKey(n => n.Id);
            e.HasIndex(n => new { n.TenantId, n.AppointmentId });
            e.HasIndex(n => new { n.ProviderId, n.ApprovedByProvider });
        });
    }
}
