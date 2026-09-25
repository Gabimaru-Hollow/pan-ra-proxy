using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Radius;

internal static class RadiusRegistration
{
    public static IServiceCollection AddRadiusAccounting(this IServiceCollection services)
    {
        services.AddSingleton(sp => RadiusClientRegistry.FromOptions(
            sp.GetRequiredService<IOptions<RadiusOptions>>().Value,
            sp.GetRequiredService<SecretLookup>(),
            sp.GetRequiredService<HostResolver>()));

        services.AddSingleton<AccountingPacketHandler>();
        services.AddHostedService<AccountingListener>();

        return services;
    }
}
