// Ticketing/Factory/TicketingProviderFactory.cs
//
// Registry + Factory pattern.
//
// Resolves the correct ITicketingProvider based on appsettings.json:
//   "Ticketing": { "ActiveProvider": "JiraCloud" }
//
// Adding a new provider in the future:
//   1. Create a new class implementing ITicketingProvider
//   2. Register it in Program.cs:
//      factory.Register("ServiceNow", sp => sp.GetRequiredService<ServiceNowProvider>())
//   3. Add its config section to TicketingOptions
//   4. Set ActiveProvider in appsettings.json
//   Zero other changes.

using BLT.Agent.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BLT.Agent.Ticketing.Factory;

public interface ITicketingProviderFactory
{
    ITicketingProvider GetActiveProvider();
    ITicketingProvider GetProvider(string providerKey);
    IEnumerable<string> GetRegisteredProviders();
}

public sealed class TicketingProviderFactory : ITicketingProviderFactory
{
    private readonly Dictionary<string, Func<ITicketingProvider>> _registry = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly TicketingOptions _options;
    private readonly ILogger          _log;

    public TicketingProviderFactory(
        IOptions<TicketingOptions> options,
        ILogger<TicketingProviderFactory> log)
    {
        _options = options.Value;
        _log     = log;
    }

    // Register a provider by key — called during startup
    public void Register(string key, Func<ITicketingProvider> factory)
    {
        _registry[key] = factory;
        _log.LogInformation("[TicketingFactory] Registered provider: {Key}", key);
    }

    // Get whichever provider is set in config
    public ITicketingProvider GetActiveProvider()
        => GetProvider(_options.ActiveProvider);

    // Get a specific provider by key (useful for testing/admin endpoints)
    public ITicketingProvider GetProvider(string providerKey)
    {
        if (_registry.TryGetValue(providerKey, out var factory))
            return factory();

        var available = string.Join(", ", _registry.Keys);
        throw new InvalidOperationException(
            $"Ticketing provider '{providerKey}' is not registered. " +
            $"Available: [{available}]. " +
            $"Check 'Ticketing:ActiveProvider' in appsettings.json.");
    }

    public IEnumerable<string> GetRegisteredProviders()
        => _registry.Keys;
}
