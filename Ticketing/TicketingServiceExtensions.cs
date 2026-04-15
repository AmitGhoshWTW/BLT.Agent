// Ticketing/TicketingServiceExtensions.cs
//
// Registers the entire ticketing subsystem with DI in one call.
//
// In Program.cs, add:
//   builder.Services.AddBltTicketing(builder.Configuration);
//
// To add a future provider:
//   services.AddScoped<ServiceNowProvider>();
//   factory.Register("ServiceNow", () => sp.GetRequiredService<ServiceNowProvider>());

using BLT.Agent.Ticketing.Contracts;
using BLT.Agent.Ticketing.Factory;
using BLT.Agent.Ticketing.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BLT.Agent.Ticketing;

public static class TicketingServiceExtensions
{
    public static IServiceCollection AddBltTicketing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind config
        services.Configure<TicketingOptions>(
            configuration.GetSection(TicketingOptions.Section));

        // Register HttpClient for each provider (separate named clients)
        services.AddHttpClient(nameof(JiraDataCenterProvider));
        services.AddHttpClient(nameof(JiraCloudProvider));
        services.AddHttpClient(nameof(AzureDevOpsProvider));

        // Register concrete providers
        services.AddScoped<JiraDataCenterProvider>();
        services.AddScoped<JiraCloudProvider>();
        services.AddScoped<AzureDevOpsProvider>();

        // Register factory as singleton (registry persists for app lifetime)
        services.AddSingleton<TicketingProviderFactory>();
        services.AddSingleton<ITicketingProviderFactory>(sp =>
        {
            var factory = sp.GetRequiredService<TicketingProviderFactory>();

            // Register all providers into the factory registry
            factory.Register("JiraDataCenter",
                () => sp.CreateScope().ServiceProvider
                        .GetRequiredService<JiraDataCenterProvider>());
            factory.Register("JiraCloud",
                () => sp.CreateScope().ServiceProvider
                        .GetRequiredService<JiraCloudProvider>());
            factory.Register("AzureDevOps",
                () => sp.CreateScope().ServiceProvider
                        .GetRequiredService<AzureDevOpsProvider>());

            // ── Add new providers here in the future ──────────────────────────
            // factory.Register("ServiceNow",
            //     () => sp.CreateScope().ServiceProvider
            //             .GetRequiredService<ServiceNowProvider>());

            return factory;
        });

        return services;
    }
}
