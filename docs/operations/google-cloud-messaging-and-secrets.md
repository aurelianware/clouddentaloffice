# Messaging and secrets on Google Cloud

Cloud Dental Office is moving from Azure Container Apps to Google Cloud Run. Until the cutover, the same images run on both clouds: two settings choose the message broker and the secret store, and Azure stays the default.

## Message broker

Bound from the `ServiceBus` section in IntakeService, SchedulingService and the Portal (the section name predates Pub/Sub).

| Setting | Azure (default) | Google Cloud |
|---|---|---|
| `ServiceBus__Provider` | `ServiceBus` | `PubSub` |
| `ServiceBus__ConnectionString` | Service Bus connection string | not used |
| `ServiceBus__PubSubProjectId` | not used | project that holds the topics |

Topic and subscription names are the same on both brokers (`booking-requests`/`scheduling`, `scheduling-availability`/`zocdoc`, `zocdoc-webhooks`/`scheduling`, `appointment-lifecycle`/`zocdoc`, `stripe-webhooks`/`portal`, `coverage-intake`/`portal`). Without the chosen provider's settings, publishing is a logged no-op and consumers stay idle, as before.

Every consumer goes through `IMessageSubscriber`, one message at a time, and settles each message by completing it, returning it for retry, or dead-lettering it with a reason. On Pub/Sub:

- The event type travels in the `subject` message attribute, where Service Bus uses the message Subject.
- Complete acks and retry nacks. Give each subscription a retry policy and a dead-letter policy (10 delivery attempts, matching Service Bus `maxDeliveryCount`) and a 14-day retention, matching the Service Bus TTL.
- Dead-letter publishes the message to `{topic}-dead-letter` with `deadLetterReason` and `deadLetterDescription` attributes, then acks. Create that topic, with a subscription to inspect it, for every topic above. Override the suffix with `ServiceBus__PubSubDeadLetterTopicSuffix`.
- The consumers pull continuously, so on Cloud Run, SchedulingService and the Portal need at least one instance with CPU always allocated.
- Local runs and tests can point at the Pub/Sub emulator with `PUBSUB_EMULATOR_HOST`.

The Portal's listen-only `CoverageIntake__ServiceBusConnectionString` applies to Service Bus only.

## Stedi keys

Per-practice keys (`stedi-apikey-{tenantId}`) and the shared pilot key keep the same secret names.

| Setting | Azure (default) | Google Cloud |
|---|---|---|
| `Stedi__KeyVaultUri` | Key Vault URI | not used |
| `Stedi__SecretManagerProjectId` | empty | project whose Secret Manager holds the keys |

When `Stedi__SecretManagerProjectId` is set, the Portal reads the `latest` version of each secret as its Cloud Run service account, which needs `roles/secretmanager.secretAccessor` on the `stedi-*` secrets only. The app never writes secrets.

Other app secrets (`KeyVault__VaultUri` on Azure) don't need code on Cloud Run: mount them from Secret Manager as environment variables on the service.
