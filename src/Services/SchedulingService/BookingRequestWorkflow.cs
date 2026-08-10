using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;
using System.Data;

public sealed class BookingRequestWorkflow(SchedulingDbContext db)
{
    public async Task<BookingRequest> MatchPatientAsync(Guid id, string tenantId, MatchBookingPatientRequest match, CancellationToken cancellationToken = default)
    {
        if (match.PatientId <= 0) throw new ArgumentException("A real patient is required.");
        var request = await FindAsync(id, tenantId, cancellationToken);
        EnsureUnresolved(request);
        request.MatchedPatientId = match.PatientId;
        request.Status = BookingRequestStatus.PatientMatched;
        request.ReviewedAt ??= DateTime.UtcNow;
        request.ReviewedBy = match.ReviewedBy;
        request.StaffNotes = match.StaffNotes;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<BookingRequest> ChangeStatusAsync(Guid id, string tenantId, ChangeBookingRequestStatusRequest change, CancellationToken cancellationToken = default)
    {
        if (change.Status is not (BookingRequestStatus.InReview or BookingRequestStatus.NeedsFollowUp or BookingRequestStatus.Rejected or BookingRequestStatus.Cancelled))
            throw new ArgumentException("Unsupported status transition.");
        var request = await FindAsync(id, tenantId, cancellationToken);
        if (request.Status == BookingRequestStatus.Approved) throw new InvalidOperationException("Approved requests cannot be changed.");
        request.Status = change.Status;
        request.ReviewedAt ??= DateTime.UtcNow;
        request.ReviewedBy = change.ReviewedBy;
        request.StaffNotes = change.StaffNotes;
        request.RejectionReason = change.Status == BookingRequestStatus.Rejected ? change.Reason : null;
        request.RejectedAt = change.Status == BookingRequestStatus.Rejected ? DateTime.UtcNow : null;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<bool> PersistEventAsync(BookingRequestedEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt.EventId == Guid.Empty || string.IsNullOrWhiteSpace(evt.TenantId) ||
            string.IsNullOrWhiteSpace(evt.Name) || string.IsNullOrWhiteSpace(evt.Phone) || evt.PreferredStartUtc == default)
            throw new ArgumentException("Required booking event fields are missing.", nameof(evt));

        if (await db.BookingRequests.AnyAsync(r => r.TenantId == evt.TenantId && r.EventId == evt.EventId, cancellationToken))
            return false;

        db.BookingRequests.Add(new BookingRequest
        {
            EventId = evt.EventId, TenantId = evt.TenantId, Name = evt.Name.Trim(), Phone = evt.Phone.Trim(),
            Email = evt.Email?.Trim(), PatientRelationship = evt.PatientRelationship,
            PreferredStartUtc = evt.PreferredStartUtc.Kind == DateTimeKind.Utc ? evt.PreferredStartUtc : evt.PreferredStartUtc.ToUniversalTime(),
            PreferredDurationMinutes = evt.DurationMinutes, Reason = evt.Reason, Message = evt.Message,
            Source = string.IsNullOrWhiteSpace(evt.Source) ? "PublicWebsite" : evt.Source,
            SourceReference = evt.SourceReference
        });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.BookingRequests.AnyAsync(r => r.TenantId == evt.TenantId && r.EventId == evt.EventId, cancellationToken)) return false;
            throw;
        }
    }

    public async Task<(BookingRequest Request, Appointment Appointment, bool Created)> ApproveAsync(
        Guid id, string tenantId, ApproveBookingRequest approval, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var request = await db.BookingRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Booking request not found.");
        if (request.ApprovedAppointmentId.HasValue)
        {
            var existing = await db.Appointments.SingleAsync(a => a.Id == request.ApprovedAppointmentId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (request, existing, false);
        }
        if (approval.PatientId <= 0) throw new ArgumentException("Select or create a patient before approval.");
        if (approval.ProviderId <= 0) throw new ArgumentException("A provider is required.");
        if (approval.DurationMinutes <= 0 || approval.StartTimeUtc == default) throw new ArgumentException("Valid scheduling details are required.");

        var start = approval.StartTimeUtc.Kind == DateTimeKind.Utc ? approval.StartTimeUtc : approval.StartTimeUtc.ToUniversalTime();
        var end = start.AddMinutes(approval.DurationMinutes);
        if (await db.Appointments.AnyAsync(a => a.ProviderId == approval.ProviderId && a.Status != AppointmentStatus.Cancelled &&
            a.StartTime < end && a.EndTime > start, cancellationToken))
            throw new InvalidOperationException("The provider already has an appointment during this time.");

        EnsureUnresolved(request);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), PatientId = approval.PatientId, ProviderId = approval.ProviderId,
            StartTime = start, EndTime = end, Status = AppointmentStatus.Scheduled,
            Notes = approval.Notes, Operatory = approval.Operatory, LocationId = approval.LocationId, CreatedAt = DateTime.UtcNow
        };
        db.Appointments.Add(appointment);
        request.MatchedPatientId = approval.PatientId;
        request.RequestedProviderId = approval.ProviderId;
        request.RequestedLocationId = approval.LocationId;
        request.ApprovedAppointmentId = appointment.Id;
        request.Status = BookingRequestStatus.Approved;
        request.ApprovedAt = DateTime.UtcNow;
        request.ApprovedBy = approval.ApprovedBy;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (request, appointment, true);
    }

    private async Task<BookingRequest> FindAsync(Guid id, string tenantId, CancellationToken cancellationToken) =>
        await db.BookingRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken)
        ?? throw new KeyNotFoundException("Booking request not found.");

    private static void EnsureUnresolved(BookingRequest request)
    {
        if (request.Status is BookingRequestStatus.Approved or BookingRequestStatus.Rejected or BookingRequestStatus.Cancelled)
            throw new InvalidOperationException("Resolved requests cannot be changed or scheduled.");
    }
}
