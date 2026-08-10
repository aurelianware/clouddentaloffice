using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Tests for the DbContext DateTime-to-UTC safety net and the exception detail
/// helper. On PostgreSQL (Npgsql), a DateTime whose Kind is not UTC is rejected
/// by "timestamp with time zone" columns and surfaces as the generic
/// "An error occurred while saving the entity changes" error. These guard against
/// regressions of that class of save failure.
/// </summary>
public class SaveNormalizationTests : IDisposable
{
    private readonly CloudDentalDbContext _dbContext;
    private const string TestTenantId = "test-tenant-utc";

    public SaveNormalizationTests()
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(p => p.TenantId).Returns(TestTenantId);
        _dbContext = new CloudDentalDbContext(options, tenantProvider.Object);
    }

    [Fact]
    public async Task SaveChanges_StampsUnspecifiedDateTimeAsUtc_WithoutShiftingClock()
    {
        // Arrange - an Unspecified-Kind DateTime is exactly what the MudBlazor
        // date/time pickers produce, and what Npgsql rejects for timestamptz.
        var localWallClock = new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified);
        var appointment = new Appointment
        {
            TenantId = TestTenantId,
            PatientId = 1,
            ProviderId = 1,
            AppointmentDateTime = localWallClock,
            DurationMinutes = 60,
            AppointmentType = "Exam",
            Status = "Scheduled",
            CreatedDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Unspecified)
        };

        // Act
        _dbContext.Appointments.Add(appointment);
        await _dbContext.SaveChangesAsync();

        // Assert - Kind is coerced to UTC (so Npgsql accepts it) but the wall-clock
        // reading is preserved (no accidental timezone shift).
        Assert.Equal(DateTimeKind.Utc, appointment.AppointmentDateTime.Kind);
        Assert.Equal(new TimeSpan(9, 30, 0), appointment.AppointmentDateTime.TimeOfDay);
        Assert.Equal(new DateTime(2026, 8, 10), appointment.AppointmentDateTime.Date);
        Assert.Equal(DateTimeKind.Utc, appointment.CreatedDate.Kind);
    }

    [Fact]
    public async Task SaveChanges_LeavesUtcDateTimeUntouched()
    {
        // Arrange
        var utc = new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);
        var procedure = new Procedure
        {
            TenantId = TestTenantId,
            PatientId = 1,
            ProviderId = 1,
            CDTCode = "D0120",
            Description = "Periodic Oral Evaluation",
            ServiceDate = utc,
            ChargeAmount = 75.00m,
            Status = "Completed",
            CreatedDate = utc
        };

        // Act
        _dbContext.Procedures.Add(procedure);
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.Equal(DateTimeKind.Utc, procedure.ServiceDate.Kind);
        Assert.Equal(utc, procedure.ServiceDate);
    }

    [Fact]
    public void GetDetailedMessage_IncludesInnerException()
    {
        var inner = new InvalidOperationException("column \"reason_for_visit\" does not exist");
        var outer = new Exception("An error occurred while saving the entity changes. See the inner exception for details.", inner);

        var message = outer.GetDetailedMessage();

        Assert.Contains("An error occurred while saving the entity changes", message);
        Assert.Contains("column \"reason_for_visit\" does not exist", message);
    }

    [Fact]
    public void GetDetailedMessage_WithoutInner_ReturnsOwnMessage()
    {
        var exception = new InvalidOperationException("Tenant ID is not available");

        Assert.Equal("Tenant ID is not available", exception.GetDetailedMessage());
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }
}
