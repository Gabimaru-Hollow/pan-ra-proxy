using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Radius;

public static class RadiusRegistration
{
    public static IServiceCollection AddRadiusAccounting(this IServiceCollection services)
    {
        services.TryAddSingleton<SecretLookup>(SecretStore.Read);
        services.TryAddSingleton<ProcessExit>(Environment.Exit);
        services.TryAddSingleton<Func<string, IPAddress[]>>(Dns.GetHostAddresses);

        services.AddSingleton(sp => RadiusClientRegistry.FromOptions(
            sp.GetRequiredService<IOptions<RadiusOptions>>().Value,
            sp.GetRequiredService<SecretLookup>(),
            sp.GetRequiredService<Func<string, IPAddress[]>>()));

        services.AddSingleton<AccountingPacketHandler>();
        services.AddHostedService<AccountingListener>();

        return services;
    }
}
