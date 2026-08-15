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
    public async Task RepeatedIdempotencyKeyCreatesOneRequestAndDifferentRequestIdsCreateSeparateRequests()
    {
        var workflow = new BookingRequestWorkflow(_db);
        var firstId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var secondId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var first = NewEvent(PatientRelationship.New) with { EventId = firstId, WebsiteRequestId = "11111111-1111-4111-8111-111111111111" };
        var second = NewEvent(PatientRelationship.New) with { EventId = secondId, WebsiteRequestId = "22222222-2222-4222-8222-222222222222" };

        Assert.True(await workflow.PersistEventAsync(first));
        Assert.False(await workflow.PersistEventAsync(first));
        Assert.True(await workflow.PersistEventAsync(second));
        Assert.Equal(2, await _db.BookingRequests.CountAsync());
    }

    [Fact]
    public async Task SameEventIdIsIsolatedByTenant()
    {
        var workflow = new BookingRequestWorkflow(_db);
        var first = NewEvent(PatientRelationship.New);
        var otherTenant = first with { TenantId = "practice-b" };

        Assert.True(await workflow.PersistEventAsync(first));
        Assert.True(await workflow.PersistEventAsync(otherTenant));
        Assert.Equal(2, await _db.BookingRequests.CountAsync());
    }

    [Fact]
    public async Task OptionalV2FieldsRoundTrip()
    {
        var submitted = DateTime.UtcNow.AddMinutes(-10);
        var alternate = DateTime.UtcNow.AddDays(2);
        var evt = NewEvent(PatientRelationship.Existing) with
        {
            WebsiteRequestId = "11111111-1111-4111-8111-111111111111",
            PreferredContact = "Text", AlternateStartUtc = alternate,
            InsuranceIntent = "Yes", InsuranceCarrier = "Delta Dental",
            Source = "google", Campaign = "implants", AttributionId = "aid-123",
            AttributionMetadata = new Dictionary<string, string> { ["utm_medium"] = "cpc" },
            SubmittedAtUtc = submitted
        };

        Assert.True(await new BookingRequestWorkflow(_db).PersistEventAsync(evt));
        var dto = (await _db.BookingRequests.SingleAsync()).ToDto();
        Assert.Equal(evt.WebsiteRequestId, dto.WebsiteRequestId);
        Assert.Equal("Text", dto.PreferredContact);
        Assert.Equal(alternate, dto.AlternateStartUtc);
        Assert.Equal("Delta Dental", dto.InsuranceCarrier);
        Assert.Equal("implants", dto.Campaign);
        Assert.Equal("aid-123", dto.AttributionId);
        Assert.Contains("utm_medium", dto.AttributionMetadataJson);
        Assert.Equal(submitted, dto.SubmittedAtUtc);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeStatusAsync(request.Id, request.TenantId,
            new ChangeBookingRequestStatusRequest(BookingRequestStatus.InReview, "staff", null, null)));
        Assert.Equal("Not accepting", request.RejectionReason);
        Assert.NotNull(request.RejectedAt);
    }

    private static BookingRequestedEvent NewEvent(PatientRelationship relationship) => new(
        "Sam Example", "4805550100", "sam@example.test", DateTime.UtcNow.AddDays(1), 45,
        "Exam", "Please call", relationship, "practice-a", "ReferenceWebsite");
}
