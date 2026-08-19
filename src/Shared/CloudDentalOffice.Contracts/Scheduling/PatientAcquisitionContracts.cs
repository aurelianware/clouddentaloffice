namespace CloudDentalOffice.Contracts.Scheduling;

public enum AcquisitionEventType
{
    LandingPageViewed,
    BookingCtaClicked,
    BookingStarted,
    AvailabilityViewed,
    BookingRequestSubmitted,
    AppointmentScheduled,
    PhoneClicked,
    FinancingClicked,
    ReviewClicked
}

public sealed record PublicAcquisitionEvent
{
    public Guid EventId { get; init; }
    public required string SessionId { get; init; }
    public required AcquisitionEventType EventType { get; init; }
    public string? Source { get; init; }
    public string? Medium { get; init; }
    public string? Campaign { get; init; }
    public string? LandingPage { get; init; }
    public string? AppointmentIntent { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record PatientAcquisitionQuery(DateTimeOffset From, DateTimeOffset To,
    string? Source = null, string? AppointmentIntent = null, string? LandingPage = null,
    Guid? LocationId = null, int? ProviderId = null);

public sealed record AcquisitionFunnelStep(string Name, long? Count, decimal? DropOffPercent);
public sealed record AcquisitionBreakdown(string Name, long? Visits, long? Starts,
    long? AvailabilityViews, long Requests, long Scheduled, decimal? RequestConversionPercent,
    decimal? ScheduleConversionPercent);
public sealed record AcquisitionDailyTotal(DateOnly Date, long Starts, long Requests, long Scheduled);

public sealed record SearchPerformanceSummary(long Clicks, long Impressions, decimal CtrPercent, decimal AveragePosition);
public sealed record SearchDailyTotal(DateOnly Date, long Clicks, long Impressions);
public sealed record SearchQueryPerformance(string Query, long Clicks, long Impressions, decimal CtrPercent, decimal AveragePosition);
public sealed record SearchDevicePerformance(string Device, long Clicks, long Impressions, decimal CtrPercent, decimal AveragePosition);
public sealed record SearchLandingPagePerformance(string LandingPage, bool IsProduction, long Clicks, long Impressions,
    decimal CtrPercent, decimal AveragePosition, long BookingStarts, long BookingRequests, long ScheduledAppointments,
    decimal? AggregateRequestRatePercent);
public sealed record SearchQueryPagePerformance(string Query, string LandingPage, long Clicks, long Impressions,
    decimal CtrPercent, decimal AveragePosition);
public sealed record SearchConsoleStatus(bool Configured, bool Enabled, string? PropertyUrl, string SyncStatus,
    DateTimeOffset? LastSuccessfulSyncAt, DateOnly? LatestImportedDate, string? LastError);
public sealed record SearchAcquisitionDashboard
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required SearchPerformanceSummary Summary { get; init; }
    public required IReadOnlyList<SearchDailyTotal> Daily { get; init; }
    public required IReadOnlyList<SearchQueryPerformance> TopQueries { get; init; }
    public required IReadOnlyList<SearchLandingPagePerformance> LandingPages { get; init; }
    public required IReadOnlyList<SearchQueryPagePerformance> QueryPages { get; init; }
    public required IReadOnlyList<SearchDevicePerformance> Devices { get; init; }
    public required SearchConsoleStatus Status { get; init; }
    public string AttributionDisclaimer { get; init; } = "Search Console metrics are aggregate. Booking metrics are compared by landing page and date range and do not identify which search query an individual patient used.";
}

public sealed record PatientAcquisitionDashboard
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public long? LandingVisits { get; init; }
    public long? BookingCtaClicks { get; init; }
    public long? BookingStarts { get; init; }
    public long? AvailabilityViews { get; init; }
    public long BookingRequests { get; init; }
    public long ScheduledAppointments { get; init; }
    public decimal? BookingConversionRate { get; init; }
    public decimal? ScheduleConversionRate { get; init; }
    public required IReadOnlyList<AcquisitionFunnelStep> Funnel { get; init; }
    public required IReadOnlyList<AcquisitionBreakdown> BySource { get; init; }
    public required IReadOnlyList<AcquisitionBreakdown> ByIntent { get; init; }
    public required IReadOnlyList<AcquisitionBreakdown> ByLandingPage { get; init; }
    public required IReadOnlyList<AcquisitionDailyTotal> Daily { get; init; }
}
