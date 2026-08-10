using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ServiceBusOptions"/> and an <see cref="IEventPublisher"/>.
    /// When a Service Bus connection string is configured, a real publisher is
    /// registered; otherwise a logging no-op is used so the app still runs.
    /// </summary>
    public static IServiceCollection AddEventPublishing(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ServiceBusOptions();
        configuration.GetSection(ServiceBusOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        if (options.IsConfigured)
        {
            services.AddSingleton<IEventPublisher>(sp => new ServiceBusEventPublisher(
                sp.GetRequiredService<ServiceBusOptions>(),
                sp.GetRequiredService<ILogger<ServiceBusEventPublisher>>()));
        }
        else
        {
            services.AddSingleton<IEventPublisher, NullEventPublisher>();
        }

        return services;
    }
}
