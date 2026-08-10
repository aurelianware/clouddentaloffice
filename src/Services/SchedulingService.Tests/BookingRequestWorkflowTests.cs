using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class BookingRequestWorkflowTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void MissingPatientRelationshipDefaultsToUnknown()
    {
        var request = JsonSerializer.Deserialize<PublicBookingRequest>("""{"name":"Sam","phone":"555","preferredStart":"2030-01-01T10:00:00Z"}""");
        Assert.Equal(PatientRelationship.Unknown, request!.PatientRelationship);
    }

    [Theory]
    [InlineData(PatientRelationship.New)]
    [InlineData(PatientRelationship.Existing)]
    [InlineData(PatientRelationship.Unknown)]
    public async Task EventCreatesRequestButNeverAppointment(PatientRelationship relationship)
    {
        var workflow = new BookingRequestWorkflow(_db);
        var evt = NewEvent(relationship);
        Assert.True(await workflow.PersistEventAsync(evt));
        Assert.Single(await _db.BookingRequests.ToListAsync());
        Assert.Empty(await _db.Appointments.ToListAsync());
        Assert.Equal(relationship, (await _db.BookingRequests.SingleAsync()).PatientRelationship);
    }

    [Fact]
    public async Task DuplicateEventIsIdempotent()
    {
        var workflow = new BookingRequestWorkflow(_db);
        var evt = NewEvent(PatientRelationship.Existing);
        Assert.True(await workflow.PersistEventAsync(evt));
        Assert.False(await workflow.PersistEventAsync(evt));
        Assert.Single(await _db.BookingRequests.ToListAsync());
    }

    [Fact]
    public async Task ApprovalRequiresPatientAndCreatesExactlyOneAppointment()
    {
        var workflow = new BookingRequestWorkflow(_db);
        var evt = NewEvent(PatientRelationship.New);
        await workflow.PersistEventAsync(evt);
        var request = await _db.BookingRequests.SingleAsync();
        var invalid = new ApproveBookingRequest { ProviderId = 1, StartTimeUtc = DateTime.UtcNow.AddDays(2), DurationMinutes = 60 };
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.ApproveAsync(request.Id, request.TenantId, invalid));

        var approval = invalid with { PatientId = 42 };
        var first = await workflow.ApproveAsync(request.Id, request.TenantId, approval);
        var second = await workflow.ApproveAsync(request.Id, request.TenantId, approval);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Appointment.Id, second.Appointment.Id);
        Assert.Single(await _db.Appointments.ToListAsync());
        Assert.Equal(BookingRequestStatus.Approved, request.Status);
        Assert.Equal(first.Appointment.Id, request.ApprovedAppointmentId);
        Assert.Equal(evt.PreferredStartUtc, request.PreferredStartUtc);
    }

    [Fact]
    public async Task MatchAndRejectNeverCreateAppointment()
    {
        var workflow = new BookingRequestWorkflow(_db);
        await workflow.PersistEventAsync(NewEvent(PatientRelationship.Unknown));
        var request = await _db.BookingRequests.SingleAsync();
        await workflow.MatchPatientAsync(request.Id, request.TenantId, new MatchBookingPatientRequest(12, "staff", null));
        Assert.Equal(12, request.MatchedPatientId);
        await workflow.ChangeStatusAsync(request.Id, request.TenantId,
            new ChangeBookingRequestStatusRequest(BookingRequestStatus.Rejected, "staff", "Not accepting", null));
        Assert.Equal(BookingRequestStatus.Rejected, request.Status);
        Assert.Empty(await _db.Appointments.ToListAsync());
    }

    private static BookingRequestedEvent NewEvent(PatientRelationship relationship) => new(
        "Sam Example", "4805550100", "sam@example.test", DateTime.UtcNow.AddDays(1), 45,
        "Exam", "Please call", relationship, "practice-a", "ReferenceWebsite");
}
