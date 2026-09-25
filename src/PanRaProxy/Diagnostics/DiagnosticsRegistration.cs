using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace PanRaProxy.Diagnostics;

public static class DiagnosticsRegistration
{
    /// <summary>
    /// The Proxy's own Event Log, under "Applications and Services Logs" in Event Viewer, instead of
    /// the shared Application log: its entries can be filtered, sized and forwarded on their own.
    /// The MSI creates it. The Proxy itself never does: a console run must leave nothing behind on the machine.
    /// </summary>
    public const string EventLogName = "PanRaProxy";

    public const string EventLogSource = "PanRaProxy";

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

    /// <summary>
    /// Why the Proxy can't write to its Event Log, or null when it can. Only reads the registry: the
    /// Proxy never creates the log or its source (the MSI does). When this isn't null, the Event Log
    /// provider must not be added at all, because it would register a missing source under the
    /// Application log on its first entry.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? EventLogProblem()
    {
        try
        {
            if (!EventLog.SourceExists(EventLogSource))
            {
                return $"the '{EventLogName}' Event Log doesn't exist on this machine: the MSI creates it.";
            }

            string current = EventLog.LogNameFromSourceName(EventLogSource, ".");

            // Source names are case-insensitive, so upstream's "PanRAProxy" under Application is the
            // same name as ours, left by an earlier install.
            return current == EventLogName
                ? null
                : $"the source '{EventLogSource}' writes to the '{current}' log, left by an earlier install. In an elevated PowerShell: "
                  + $"[System.Diagnostics.EventLog]::DeleteEventSource('{EventLogSource}'), then repair the MSI.";
        }
        catch (Exception ex)
        {
            // A session without administrator rights can't search every log for a missing source.
            return $"the '{EventLogName}' Event Log can't be checked from this session: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(EventLogSettings settings)
    {
        settings.SourceName = EventLogSource;
        settings.LogName = EventLogName;
    }
}
