using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

[Index(nameof(TenantId), nameof(EventId), IsUnique = true)]
[Index(nameof(TenantId), nameof(OccurredAt))]
[Index(nameof(TenantId), nameof(EventType), nameof(OccurredAt))]
[Index(nameof(TenantId), nameof(Source), nameof(OccurredAt))]
[Index(nameof(TenantId), nameof(AppointmentIntent), nameof(OccurredAt))]
public sealed class PatientAcquisitionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(128)] public string SessionId { get; set; } = string.Empty;
    public AcquisitionEventType EventType { get; set; }
    [MaxLength(40)] public string Source { get; set; } = "unknown";
    [MaxLength(40)] public string? Medium { get; set; }
    [MaxLength(120)] public string? Campaign { get; set; }
    [MaxLength(300)] public string? LandingPage { get; set; }
    [MaxLength(64)] public string AppointmentIntent { get; set; } = "other";
    public Guid? BookingRequestId { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? LocationId { get; set; }
    public int? ProviderId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public static class AcquisitionVocabulary
{
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
    { "google-organic", "google-business", "direct", "referral", "zocdoc", "website", "social", "staff", "unknown", "other" };
    private static readonly HashSet<string> Intents = new(StringComparer.OrdinalIgnoreCase)
    { "new-patient", "emergency", "implant-consult", "full-arch", "cosmetic-consult", "cleaning", "other" };

    public static string Source(string? value, string? medium = null)
    {
        var source = Clean(value, 40)?.ToLowerInvariant();
        if (source is "google-business") return source;
        if (source is "google" && string.Equals(medium, "organic", StringComparison.OrdinalIgnoreCase)) return "google-organic";
        if (Sources.Contains(source ?? "")) return source!;
        return source is "emergency" or "implants" or "full-arch" or "cosmetic" or "new-patient-offer" or "homepage" ? "website" : "other";
    }

    public static string Intent(string? value)
    {
        var intent = Clean(value, 64)?.ToLowerInvariant();
        if (intent is "new-patient-exam") intent = "new-patient";
        return Intents.Contains(intent ?? "") ? intent! : "other";
    }

    public static string? Path(string? value)
    {
        var candidate = Clean(value, 300);
        if (candidate is null) return null;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute)) candidate = absolute.AbsolutePath;
        candidate = candidate.Split('?', '#')[0];
        return candidate.StartsWith('/') && !candidate.Contains("..", StringComparison.Ordinal) ? candidate : null;
    }

    public static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}

public interface IPatientAcquisitionService
{
    Task<bool> RecordWebsiteAsync(string tenantId, PublicAcquisitionEvent input, CancellationToken cancellationToken = default);
    Task RecordBookingRequestAsync(BookingRequest request, CancellationToken cancellationToken = default);
    Task RecordScheduledAsync(BookingRequest request, Appointment appointment, CancellationToken cancellationToken = default);
    Task<PatientAcquisitionDashboard> GetDashboardAsync(string tenantId, PatientAcquisitionQuery query, CancellationToken cancellationToken = default);
}

public sealed class PatientAcquisitionService(SchedulingDbContext db, TimeProvider clock) : IPatientAcquisitionService
{
    public async Task<bool> RecordWebsiteAsync(string tenantId, PublicAcquisitionEvent input, CancellationToken cancellationToken = default)
    {
        if (input.EventId == Guid.Empty || !ValidSession(input.SessionId)) throw new ArgumentException("A valid event and opaque session ID are required.");
        var occurred = input.OccurredAt == default ? clock.GetUtcNow() : input.OccurredAt;
        if (occurred < clock.GetUtcNow().AddDays(-7) || occurred > clock.GetUtcNow().AddMinutes(10)) throw new ArgumentException("Event timestamp is outside the accepted window.");
        if (input.EventType is AcquisitionEventType.BookingRequestSubmitted or AcquisitionEventType.AppointmentScheduled)
            throw new ArgumentException("Business outcome events are server-generated.");
        if (await db.PatientAcquisitionEvents.AnyAsync(x => x.TenantId == tenantId && x.EventId == input.EventId, cancellationToken)) return false;
        db.PatientAcquisitionEvents.Add(new()
        {
            EventId = input.EventId, TenantId = tenantId, SessionId = input.SessionId.Trim(), EventType = input.EventType,
            Source = AcquisitionVocabulary.Source(input.Source, input.Medium), Medium = AcquisitionVocabulary.Clean(input.Medium, 40),
            Campaign = AcquisitionVocabulary.Clean(input.Campaign, 120), LandingPage = AcquisitionVocabulary.Path(input.LandingPage),
            AppointmentIntent = AcquisitionVocabulary.Intent(input.AppointmentIntent), OccurredAt = occurred.UtcDateTime
        });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task RecordBookingRequestAsync(BookingRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = Metadata(request.AttributionMetadataJson);
        await RecordBusiness(new()
        {
            EventId = request.EventId, TenantId = request.TenantId, SessionId = request.AttributionId ?? $"request:{request.Id:N}",
            EventType = AcquisitionEventType.BookingRequestSubmitted,
            Source = AcquisitionVocabulary.Source(request.Source, metadata.GetValueOrDefault("utm_medium")),
            Medium = AcquisitionVocabulary.Clean(metadata.GetValueOrDefault("utm_medium"), 40), Campaign = AcquisitionVocabulary.Clean(request.Campaign, 120),
            LandingPage = AcquisitionVocabulary.Path(metadata.GetValueOrDefault("landing_page")),
            AppointmentIntent = AcquisitionVocabulary.Intent(request.RequestedAppointmentTypeId), BookingRequestId = request.Id,
            LocationId = request.RequestedLocationId, ProviderId = request.RequestedProviderId, OccurredAt = request.SubmittedAtUtc
        }, cancellationToken);
    }

    public Task RecordScheduledAsync(BookingRequest request, Appointment appointment, CancellationToken cancellationToken = default) => RecordBusiness(new()
    {
        EventId = appointment.Id, TenantId = request.TenantId, SessionId = request.AttributionId ?? $"request:{request.Id:N}",
        EventType = AcquisitionEventType.AppointmentScheduled, Source = AcquisitionVocabulary.Source(request.Source),
        Campaign = AcquisitionVocabulary.Clean(request.Campaign, 120), AppointmentIntent = AcquisitionVocabulary.Intent(request.RequestedAppointmentTypeId),
        BookingRequestId = request.Id, AppointmentId = appointment.Id, LocationId = appointment.LocationId,
        ProviderId = appointment.ProviderId, OccurredAt = request.ApprovedAt ?? appointment.CreatedAt
    }, cancellationToken);

    private async Task RecordBusiness(PatientAcquisitionEvent item, CancellationToken cancellationToken)
    {
        if (await db.PatientAcquisitionEvents.AnyAsync(x => x.TenantId == item.TenantId && x.EventId == item.EventId, cancellationToken)) return;
        db.PatientAcquisitionEvents.Add(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PatientAcquisitionDashboard> GetDashboardAsync(string tenantId, PatientAcquisitionQuery query, CancellationToken cancellationToken = default)
    {
        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a valid reporting range up to 366 days.");
        var filtered = db.PatientAcquisitionEvents.AsNoTracking().Where(x => x.TenantId == tenantId && x.OccurredAt >= query.From.UtcDateTime && x.OccurredAt < query.To.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Source)) filtered = filtered.Where(x => x.Source == query.Source);
        if (!string.IsNullOrWhiteSpace(query.AppointmentIntent)) filtered = filtered.Where(x => x.AppointmentIntent == query.AppointmentIntent);
        if (!string.IsNullOrWhiteSpace(query.LandingPage)) filtered = filtered.Where(x => x.LandingPage == query.LandingPage);
        if (query.LocationId.HasValue) filtered = filtered.Where(x => x.LocationId == query.LocationId);
        if (query.ProviderId.HasValue) filtered = filtered.Where(x => x.ProviderId == query.ProviderId);
        var events = await filtered.Select(x => new ReportEvent(x.SessionId, x.EventType, x.Source, x.AppointmentIntent, x.LandingPage, x.OccurredAt)).ToListAsync(cancellationToken);
        long Unique(AcquisitionEventType type) => events.Where(x => x.EventType == type).Select(x => x.SessionId).Distinct().LongCount();
        var upstreamMeasured = events.Any(x => x.EventType <= AcquisitionEventType.AvailabilityViewed);
        long? visits = upstreamMeasured ? Unique(AcquisitionEventType.LandingPageViewed) : null;
        long? ctas = upstreamMeasured ? Unique(AcquisitionEventType.BookingCtaClicked) : null;
        long? starts = upstreamMeasured ? Unique(AcquisitionEventType.BookingStarted) : null;
        long? availability = upstreamMeasured ? Unique(AcquisitionEventType.AvailabilityViewed) : null;
        var requests = Unique(AcquisitionEventType.BookingRequestSubmitted); var scheduled = Unique(AcquisitionEventType.AppointmentScheduled);
        var sequence = new (string Name, long? Count)[] { ("Landing visits", visits), ("Booking CTA clicks", ctas), ("Booking starts", starts), ("Availability views", availability), ("Booking requests", requests), ("Scheduled appointments", scheduled) };
        var funnel = sequence.Select((x, i) => new AcquisitionFunnelStep(x.Name, x.Count, i == 0 ? null : Drop(sequence[i - 1].Count, x.Count))).ToArray();
        return new()
        {
            From = query.From, To = query.To, LandingVisits = visits, BookingCtaClicks = ctas, BookingStarts = starts,
            AvailabilityViews = availability, BookingRequests = requests, ScheduledAppointments = scheduled,
            BookingConversionRate = Rate(requests, starts), ScheduleConversionRate = Rate(scheduled, requests), Funnel = funnel,
            BySource = Breakdown(events, x => x.Source, upstreamMeasured),
            ByIntent = Breakdown(events, x => x.AppointmentIntent, upstreamMeasured),
            ByLandingPage = Breakdown(events, x => x.LandingPage ?? "Unknown", upstreamMeasured),
            Daily = events.GroupBy(x => DateOnly.FromDateTime(x.OccurredAt)).OrderBy(x => x.Key).Select(x => new AcquisitionDailyTotal(x.Key,
                x.Where(e => e.EventType == AcquisitionEventType.BookingStarted).Select(e => e.SessionId).Distinct().LongCount(),
                x.Where(e => e.EventType == AcquisitionEventType.BookingRequestSubmitted).Select(e => e.SessionId).Distinct().LongCount(),
                x.Where(e => e.EventType == AcquisitionEventType.AppointmentScheduled).Select(e => e.SessionId).Distinct().LongCount())).ToArray()
        };
    }

    private static IReadOnlyList<AcquisitionBreakdown> Breakdown(IEnumerable<ReportEvent> source, Func<ReportEvent, string> key, bool upstream)
    {
        var rows = source.GroupBy(key);
        return rows.Select(g =>
        {
            long Count(AcquisitionEventType t) => g.Where(x => x.EventType == t).Select(x => x.SessionId).Distinct().LongCount();
            var requests = Count(AcquisitionEventType.BookingRequestSubmitted); var scheduled = Count(AcquisitionEventType.AppointmentScheduled);
            long? visits = upstream ? Count(AcquisitionEventType.LandingPageViewed) : null;
            long? starts = upstream ? Count(AcquisitionEventType.BookingStarted) : null;
            long? views = upstream ? Count(AcquisitionEventType.AvailabilityViewed) : null;
            return new AcquisitionBreakdown(g.Key, visits, starts, views, requests, scheduled, Rate(requests, starts), Rate(scheduled, requests));
        }).OrderByDescending(x => x.Scheduled).ThenByDescending(x => x.Requests).ToArray();
    }

    private static bool ValidSession(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 16 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static decimal? Rate(long numerator, long? denominator) => denominator is > 0 ? Math.Round(numerator * 100m / denominator.Value, 1) : null;
    private static decimal? Drop(long? previous, long? current) => previous is > 0 && current.HasValue ? Math.Round((previous.Value - current.Value) * 100m / previous.Value, 1) : null;
    private static Dictionary<string, string> Metadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; } catch (JsonException) { return []; }
    }
    private sealed record ReportEvent(string SessionId, AcquisitionEventType EventType, string Source,
        string AppointmentIntent, string? LandingPage, DateTime OccurredAt);
}
