using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace PanRaProxy.Diagnostics;

public static class DiagnosticsRegistration
{
    /// <summary>
    /// The Proxy's own Event Log, under "Applications and Services Logs" in Event Viewer, instead of
    /// the shared Application log: its entries can be filtered, sized and forwarded on their own.
    /// The MSI creates it; a console run creates it too when started elevated.
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
    /// Registers the Event Log and its source when they don't exist yet, which needs administrator
    /// rights. Returns the reason it couldn't, or null on success: the Proxy runs either way, it just
    /// logs to the console and to file instead.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? EnsureEventLog()
    {
        try
        {
            if (EventLog.SourceExists(EventLogSource))
            {
                string current = EventLog.LogNameFromSourceName(EventLogSource, ".");

                // Source names are case-insensitive, so upstream's "PanRAProxy" under Application is the
                // same name as ours. Entries keep going to that log until the old source is removed.
                return current == EventLogName
                    ? null
                    : $"the source '{EventLogSource}' still writes to the '{current}' log, left by an earlier install, "
                      + $"so entries go there instead of the '{EventLogName}' log. In an elevated PowerShell: "
                      + $"[System.Diagnostics.EventLog]::DeleteEventSource('{EventLogSource}'), then start the Proxy again.";
            }

            EventLog.CreateEventSource(new EventSourceCreationData(EventLogSource, EventLogName));
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return $"the Event Log '{EventLogName}' doesn't exist and creating it needs an elevated session.";
        }
        catch (Exception ex)
        {
            return $"the Event Log '{EventLogName}' is unavailable: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(EventLogSettings settings)
    {
        settings.SourceName = EventLogSource;
        settings.LogName = EventLogName;
    }
}
