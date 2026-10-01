using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Firewall;

internal static class FirewallRegistration
{
    /// <summary>
    /// The Batch sender, and where it sends: the Firewalls, or nowhere in a dry run. A dry run registers no
    /// <see cref="FirewallClient"/>, so it needs no Firewall settings and no API key.
    /// </summary>
    public static IServiceCollection AddFirewallSubmission(this IServiceCollection services, bool dryRun)
    {
        services.AddHostedService<BatchSender>();

        if (dryRun)
        {
            services.AddSingleton<IBatchSubmitter, DryRunSubmitter>();
            return services;
        }

        services.AddSingleton<IBatchSubmitter>(sp => sp.GetRequiredService<FirewallClient>());
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

        return services;
    }
}
