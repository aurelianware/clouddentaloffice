namespace CloudDentalOffice.Messaging;

/// <summary>
/// Configuration for the Service Bus connection. Bound from the "ServiceBus"
/// section. When <see cref="ConnectionString"/> is empty, messaging is treated
/// as not configured and a no-op publisher is used (so services still run
/// locally without a broker).
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public string? ConnectionString { get; set; }

    /// <summary>Topic that booking-request events are published to.</summary>
    public string BookingTopic { get; set; } = "booking-requests";

    /// <summary>Subscription the SchedulingService consumer reads from.</summary>
    public string BookingSubscription { get; set; } = "scheduling";

    public string SchedulingAvailabilityTopic { get; set; } = "scheduling-availability";
    public string SchedulingAvailabilitySubscription { get; set; } = "zocdoc";
    public string ZocdocWebhookTopic { get; set; } = "zocdoc-webhooks";
    public string ZocdocWebhookSubscription { get; set; } = "scheduling";
    public string AppointmentLifecycleTopic { get; set; } = "appointment-lifecycle";
    public string AppointmentLifecycleSubscription { get; set; } = "zocdoc";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
