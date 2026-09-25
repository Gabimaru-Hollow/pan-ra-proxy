using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace PanRaProxy.Diagnostics;

public static class DiagnosticsRegistration
{
    /// <summary>
    /// Upstream's Event Lg source, kept so existing Event Log filters and alerts still match (NFR-07).
    /// The install script registers it; the Application log is used.
    /// </summary>
    public const string EventLogSource = "PanRAProxy";

    public static IServiceCollection AddProxyDiagnostics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<ProxyMetrics>();

        if (OperatingSystem.IsWindows())
        {
            services.Configure<EventLogSettings>(ConfigureEventLog);
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(EventLogSettings settings)
    {
        settings.SourceName = EventLogSource;
        settings.LogName = "Application";
    }
}
