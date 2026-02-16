using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Integration tests for all save operations to catch FK constraint and database issues
/// </summary>
public class SaveOperationsIntegrationTests : IDisposable
{
    private readonly CloudDentalDbContext _dbContext;
    private readonly string _testTenantId = "test-tenant-001";

    public SaveOperationsIntegrationTests()
    {
        // Use in-memory database for testing
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _dbContext = new CloudDentalDbContext(options);
        
        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        // Seed Providers
        var provider = new Provider
        {
            ProviderId = 1,
            TenantId = _testTenantId,
            FirstName = "Sarah",
            LastName = "Smile",
            NPI = "1234567890",
            LicenseNumber = "DDS-12345",
            LicenseState = "CA",
            Specialty = "General Dentistry",
            IsActive = true,
            CreatedDate = DateTime.UtcNow
        };
        _dbContext.Providers.Add(provider);

        // Seed Procedure Codes
        var procedureCodes = new List<ProcedureCode>
        {
            new ProcedureCode
            {
                ProcedureCodeId = 1,
                TenantId = _testTenantId,
                Code = "D0120",
                Description = "Periodic Oral Evaluation",
                Category = "Diagnostic",
                DefaultFee = 75.00m
            },
            new ProcedureCode
            {
                ProcedureCodeId = 2,
                TenantId = _testTenantId,
                Code = "D1110",
                Description = "Adult Prophylaxis",
                Category = "Preventive",
                DefaultFee = 120.00m
            },
            new ProcedureCode
            {
                ProcedureCodeId = 3,
                TenantId = _testTenantId,
                Code = "D2391",
                Description = "Resin 1 Surface Posterior",
                Category = "Restorative",
                DefaultFee = 185.00m
            }
        };
        _dbContext.ProcedureCodes.AddRange(procedureCodes);

        _dbContext.SaveChanges();
    }

    [Fact]
    public async Task Test_PatientSave_ShouldSucceed()
    {
        // Arrange
        var patient = new Patient
        {
            TenantId = _testTenantId,
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = new DateTime(1980, 5, 15),
            Gender = "Male",
            Email = "john.doe@example.com",
            PrimaryPhone = "555-1234",
            Address1 = "123 Main St",
            City = "San Francisco",
            State = "CA",
            ZipCode = "94102",
            SSN = "123-45-6789",
            Status = "Active",
            CreatedDate = DateTime.UtcNow
        };

        // Act
        _dbContext.Patients.Add(patient);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.True(result > 0, "Patient save should affect at least 1 row");
        Assert.True(patient.PatientId > 0, "Patient should have ID assigned");
        
        var savedPatient = await _dbContext.Patients.FindAsync(patient.PatientId);
        Assert.NotNull(savedPatient);
        Assert.Equal("John", savedPatient.FirstName);
        Assert.Equal("Doe", savedPatient.LastName);
    }

    [Fact]
    public async Task Test_AppointmentSave_WithoutForeignKeys_ShouldSucceed()
    {
        // Arrange - Create appointment WITHOUT FK navigation properties
        // This simulates microservices pattern where patient/provider are in separate DBs
        var appointment = new Appointment
        {
            TenantId = _testTenantId,
            PatientId = 1, // Just store the ID, no FK constraint
            ProviderId = 1, // Just store the ID, no FK constraint
            AppointmentDate = DateTime.Today.AddDays(7),
            AppointmentTime = new TimeSpan(9, 0, 0),
            Duration = 60,
            AppointmentType = "Exam",
            Status = "Scheduled",
            ReasonForVisit = "Annual checkup",
            CreatedDate = DateTime.UtcNow
        };

        // Act
        _dbContext.Appointments.Add(appointment);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.True(result > 0, "Appointment save should succeed without FK constraints");
        Assert.True(appointment.AppointmentId > 0, "Appointment should have ID assigned");
        
        var savedAppt = await _dbContext.Appointments.FindAsync(appointment.AppointmentId);
        Assert.NotNull(savedAppt);
        Assert.Equal(1, savedAppt.PatientId);
        Assert.Equal(1, savedAppt.ProviderId);
        Assert.Equal("Scheduled", savedAppt.Status);
    }

    [Fact]
    public async Task Test_TreatmentPlanSave_WithoutForeignKeys_ShouldSucceed()
    {
        // Arrange
        var treatmentPlan = new TreatmentPlan
        {
            TenantId = _testTenantId,
            PatientId = 1, // Just ID, no FK
            ProviderId = 1, // Just ID, no FK
            Title = "Comprehensive Treatment Plan",
            Description = "Full mouth restoration",
            Status = "Draft",
            CreatedDate = DateTime.UtcNow,
            PlannedProcedures = new List<PlannedProcedure>
            {
                new PlannedProcedure
                {
                    TenantId = _testTenantId,
                    CDTCode = "D2391",
                    Description = "Resin 1 Surface Posterior",
                    ToothNumber = "14",
                    EstimatedFee = 185.00m,
                    Status = "Planned",
                    SequenceNumber = 1,
                    CreatedDate = DateTime.UtcNow
                }
            }
        };

        // Act
        _dbContext.TreatmentPlans.Add(treatmentPlan);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.True(result > 0, "Treatment plan save should succeed");
        Assert.True(treatmentPlan.TreatmentPlanId > 0);
        Assert.Single(treatmentPlan.PlannedProcedures);
        
        var savedPlan = await _dbContext.TreatmentPlans
            .Include(tp => tp.PlannedProcedures)
            .FirstOrDefaultAsync(tp => tp.TreatmentPlanId == treatmentPlan.TreatmentPlanId);
        
        Assert.NotNull(savedPlan);
        Assert.Equal("Comprehensive Treatment Plan", savedPlan.Title);
        Assert.Single(savedPlan.PlannedProcedures);
    }

    [Fact]
    public async Task Test_ClaimSave_WithProcedures_ShouldSucceed()
    {
        // Arrange
        var claim = new Claim
        {
            TenantId = _testTenantId,
            ClaimNumber = "CLM-20260216-TEST001",
            PatientId = 1,
            ProviderId = 1,
            ServiceDateFrom = DateTime.Today,
            ClaimType = "Primary",
            Status = "Draft",
            TotalChargeAmount = 245.00m,
            CreatedDate = DateTime.UtcNow,
            Procedures = new List<ClaimProcedure>
            {
                new ClaimProcedure
                {
                    TenantId = _testTenantId,
                    CDTCode = "D0120",
                    Description = "Periodic Oral Evaluation",
                    ServiceDate = DateTime.Today,
                    ChargeAmount = 75.00m,
                    LineNumber = 1,
                    CreatedDate = DateTime.UtcNow
                },
                new ClaimProcedure
                {
                    TenantId = _testTenantId,
                    CDTCode = "D2391",
                    Description = "Resin 1 Surface Posterior",
                    ServiceDate = DateTime.Today,
                    ToothNumber = "14",
                    ChargeAmount = 185.00m,
                    LineNumber = 2,
                    CreatedDate = DateTime.UtcNow
                }
            }
        };

        // Act
        _dbContext.Claims.Add(claim);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.True(result > 0, "Claim save should succeed");
        Assert.True(claim.ClaimId > 0);
        Assert.Equal(2, claim.Procedures.Count);
        
        var savedClaim = await _dbContext.Claims
            .Include(c => c.Procedures)
            .FirstOrDefaultAsync(c => c.ClaimId == claim.ClaimId);
        
        Assert.NotNull(savedClaim);
        Assert.Equal("CLM-20260216-TEST001", savedClaim.ClaimNumber);
        Assert.Equal(2, savedClaim.Procedures.Count);
        Assert.Equal(245.00m, savedClaim.TotalChargeAmount);
    }

    [Fact]
    public async Task Test_ProviderSave_ShouldSucceed()
    {
        // Arrange
        var provider = new Provider
        {
            TenantId = _testTenantId,
            FirstName = "Michael",
            LastName = "Chen",
            NPI = "9876543210",
            LicenseNumber = "DDS-54321",
            LicenseState = "NY",
            Specialty = "Orthodontics",
            Email = "mchen@example.com",
            Phone = "555-9999",
            IsActive = true,
            CreatedDate = DateTime.UtcNow
        };

        // Act
        _dbContext.Providers.Add(provider);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.True(result > 0, "Provider save should succeed");
        Assert.True(provider.ProviderId > 0);
        
        var savedProvider = await _dbContext.Providers.FindAsync(provider.ProviderId);
        Assert.NotNull(savedProvider);
        Assert.Equal("Michael", savedProvider.FirstName);
        Assert.Equal("9876543210", savedProvider.NPI);
    }

    [Fact]
    public async Task Test_ProcedureCodeSearch_ShouldReturnResults()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<ProcedureCodeService>>();
        var service = new ProcedureCodeService(_dbContext, mockLogger.Object);

        // Act - Search by code
        var resultsByCode = await service.SearchProcedureCodesAsync("D0120");

        // Assert
        Assert.NotEmpty(resultsByCode);
        Assert.Contains(resultsByCode, pc => pc.Code == "D0120");
        Assert.Contains(resultsByCode, pc => pc.Description.Contains("Oral Evaluation"));

        // Act - Search by description
        var resultsByDesc = await service.SearchProcedureCodesAsync("prophylaxis");

        // Assert
        Assert.NotEmpty(resultsByDesc);
        Assert.Contains(resultsByDesc, pc => pc.Code == "D1110");

        // Act - Empty search (should return top results)
        var topResults = await service.SearchProcedureCodesAsync("");

        // Assert
        Assert.NotEmpty(topResults);
        Assert.True(topResults.Count() <= 20, "Should limit results to 20");
    }

    [Fact]
    public async Task Test_MultipleAppointments_SameDayDifferentTimes_ShouldSucceed()
    {
        // Arrange - Test overlapping appointment handling
        var appointment1 = new Appointment
        {
            TenantId = _testTenantId,
            PatientId = 1,
            ProviderId = 1,
            AppointmentDate = DateTime.Today.AddDays(1),
            AppointmentTime = new TimeSpan(9, 0, 0),
            Duration = 60,
            AppointmentType = "Exam",
            Status = "Scheduled",
            CreatedDate = DateTime.UtcNow
        };

        var appointment2 = new Appointment
        {
            TenantId = _testTenantId,
            PatientId = 2,
            ProviderId = 1,
            AppointmentDate = DateTime.Today.AddDays(1),
            AppointmentTime = new TimeSpan(10, 30, 0),
            Duration = 30,
            AppointmentType = "Cleaning",
            Status = "Scheduled",
            CreatedDate = DateTime.UtcNow
        };

        // Act
        _dbContext.Appointments.AddRange(appointment1, appointment2);
        var result = await _dbContext.SaveChangesAsync();

        // Assert
        Assert.Equal(2, result);
        
        var appointments = await _dbContext.Appointments
            .Where(a => a.AppointmentDate == DateTime.Today.AddDays(1))
            .OrderBy(a => a.AppointmentTime)
            .ToListAsync();
        
        Assert.Equal(2, appointments.Count);
        Assert.Equal(new TimeSpan(9, 0, 0), appointments[0].AppointmentTime);
        Assert.Equal(new TimeSpan(10, 30, 0), appointments[1].AppointmentTime);
    }

    [Fact]
    public async Task Test_TenantIsolation_DifferentTenants_ShouldIsolate()
    {
        // Arrange
        var tenant1Patient = new Patient
        {
            TenantId = "tenant-001",
            FirstName = "Alice",
            LastName = "Smith",
            DateOfBirth = new DateTime(1990, 1, 1),
            Gender = "Female",
            Status = "Active",
            CreatedDate = DateTime.UtcNow
        };

        var tenant2Patient = new Patient
        {
            TenantId = "tenant-002",
            FirstName = "Bob",
            LastName = "Jones",
            DateOfBirth = new DateTime(1985, 6, 15),
            Gender = "Male",
            Status = "Active",
            CreatedDate = DateTime.UtcNow
        };

        // Act
        _dbContext.Patients.AddRange(tenant1Patient, tenant2Patient);
        await _dbContext.SaveChangesAsync();

        // Assert - Query with tenant filter
        var tenant1Patients = await _dbContext.Patients
            .Where(p => p.TenantId == "tenant-001")
            .ToListAsync();
        
        var tenant2Patients = await _dbContext.Patients
            .Where(p => p.TenantId == "tenant-002")
            .ToListAsync();

        Assert.Single(tenant1Patients);
        Assert.Equal("Alice", tenant1Patients[0].FirstName);
        
        Assert.Single(tenant2Patients);
        Assert.Equal("Bob", tenant2Patients[0].FirstName);
    }

    [Fact]
    public async Task Test_AppointmentService_CreateAppointment_ShouldSucceed()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<AppointmentServiceImpl>>();
        var service = new AppointmentServiceImpl(_dbContext, mockLogger.Object);

        var appointment = new Appointment
        {
            TenantId = _testTenantId,
            PatientId = 1,
            ProviderId = 1,
            AppointmentDate = DateTime.Today.AddDays(5),
            AppointmentTime = new TimeSpan(14, 0, 0),
            Duration = 45,
            AppointmentType = "Follow-up",
            Status = "Scheduled",
            ReasonForVisit = "Check filling",
            CreatedDate = DateTime.UtcNow
        };

        // Act
        var result = await service.CreateAppointmentAsync(appointment);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.AppointmentId > 0);
        Assert.Equal("Scheduled", result.Status);
        
        // Verify it's actually in the database
        var saved = await _dbContext.Appointments.FindAsync(result.AppointmentId);
        Assert.NotNull(saved);
        Assert.Equal("Follow-up", saved.AppointmentType);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
    }
}
