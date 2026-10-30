using CloudDentalOffice.Contracts.Events;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Message broker settings, bound from the "ServiceBus" section (the name
/// predates Pub/Sub support). <see cref="Provider"/> picks Azure Service Bus
/// (the default) or Google Cloud Pub/Sub; topic and subscription names are the
/// same on both. When the chosen provider has no connection settings,
/// messaging is treated as not configured and a no-op publisher is used (so
/// services still run locally without a broker).
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public const string ServiceBusProvider = "ServiceBus";
    public const string PubSubProvider = "PubSub";

    /// <summary>"ServiceBus" (default) or "PubSub".</summary>
    public string Provider { get; set; } = ServiceBusProvider;

    /// <summary>Service Bus namespace connection string (Provider = ServiceBus).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Google Cloud project that holds the topics (Provider = PubSub).</summary>
    public string? PubSubProjectId { get; set; }

    /// <summary>
    /// Pub/Sub has no built-in dead-letter call, so a dead-lettered message is
    /// published to "{topic}{suffix}" with the reason as attributes, then acknowledged.
    /// </summary>
    public string PubSubDeadLetterTopicSuffix { get; set; } = "-dead-letter";

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
    public string StripeWebhookTopic { get; set; } = "stripe-webhooks";
    public string StripeWebhookSubscription { get; set; } = "portal";
    public string CoverageIntakeTopic { get; set; } = "coverage-intake";
    public string CoverageIntakeSubscription { get; set; } = "portal";

    public bool IsPubSub => string.Equals(Provider, PubSubProvider, StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => IsPubSub
        ? !string.IsNullOrWhiteSpace(PubSubProjectId)
        : !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>The topic an event is published to.</summary>
    public string TopicFor(IntegrationEvent @event) => @event switch
    {
        SchedulingAvailabilityChangedEvent => SchedulingAvailabilityTopic,
        ZocdocAppointmentWebhookEvent => ZocdocWebhookTopic,
        AppointmentLifecycleChangedEvent => AppointmentLifecycleTopic,
        StripePaymentWebhookEvent or StripeRefundWebhookEvent or StripeDisputeWebhookEvent => StripeWebhookTopic,
        CoverageIntakeSubmittedEvent or WebsiteInsuranceCheckEvent => CoverageIntakeTopic,
        _ => BookingTopic
    };
}
