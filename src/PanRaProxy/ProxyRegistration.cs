using System.Net;
using PanRaProxy.Diagnostics;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy;

public static class ProxyRegistration
{
    /// <summary>
    /// The whole Proxy: every module and the adapters over the machine they share. The host adds the rest
    /// (Windows service lifetime, configuration sources, logging providers). Tests compose this same graph
    /// and replace adapters afterwards with <c>services.Replace</c>, so the order of registrations never matters.
    /// </summary>
    public static IServiceCollection AddPanRaProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<SecretLookup>(SecretStore.Read);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProcessExit>(Environment.Exit);
        services.AddSingleton<HostResolver>(Dns.GetHostAddresses);

        return services
            .AddProxyOptions(configuration)
            .AddProxyDiagnostics()
            .AddMappingDecision()
            .AddRadiusAccounting()
            .AddFirewallSubmission();
    }
}
