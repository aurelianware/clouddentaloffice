using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ServiceBusOptions"/>, an <see cref="IEventPublisher"/>
    /// and an <see cref="IMessageSubscriber"/> for the configured provider
    /// (ServiceBus:Provider = ServiceBus or PubSub). When the provider has no
    /// connection settings, a logging no-op publisher is used so the app still
    /// runs, and consumers stay idle.
    /// </summary>
    public static IServiceCollection AddEventPublishing(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ServiceBusOptions();
        configuration.GetSection(ServiceBusOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        if (!options.IsConfigured)
        {
            services.AddSingleton<IEventPublisher, NullEventPublisher>();
            services.AddSingleton<IMessageSubscriber, NullMessageSubscriber>();
        }
        else if (options.IsPubSub)
        {
            services.AddSingleton<PubSubEventPublisher>();
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<PubSubEventPublisher>());
            services.AddSingleton<IMessageSubscriber, PubSubMessageSubscriber>();
        }
        else
        {
            services.AddSingleton<IEventPublisher>(sp => new ServiceBusEventPublisher(
                sp.GetRequiredService<ServiceBusOptions>(),
                sp.GetRequiredService<ILogger<ServiceBusEventPublisher>>()));
            services.AddSingleton<IMessageSubscriber>(sp => new ServiceBusMessageSubscriber(
                options.ConnectionString!, sp.GetRequiredService<ILogger<ServiceBusMessageSubscriber>>()));
        }

        return services;
    }
}
