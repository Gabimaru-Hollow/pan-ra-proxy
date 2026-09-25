using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Firewall;

internal static class FirewallRegistration
{
    public static IServiceCollection AddFirewallSubmission(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            FirewallOptions options = sp.GetRequiredService<IOptions<FirewallOptions>>().Value;

            // One long-lived HttpClient: FirewallClient keeps the active Firewall across Batches.
            // PooledConnectionLifetime picks up DNS changes (e.g. after an HA failover).
            SocketsHttpHandler handler = new()
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = { RemoteCertificateValidationCallback = CertificateTrust.Create(options) },
            };

            return ActivatorUtilities.CreateInstance<FirewallClient>(sp, new HttpClient(handler));
        });

        services.AddHostedService<BatchSender>();

        return services;
    }
}
