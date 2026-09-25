using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;

namespace PanRaProxy.Options;

public static class StartupValidation
{
    /// <summary>
    /// Validates every options section and logs all failures together (event 3106), so an administrator
    /// sees the whole list instead of the first problem only. False means the Proxy must not start.
    /// </summary>
    public static bool Validate(IServiceProvider services)
    {
        List<string> failures = [];

        Collect<RadiusOptions>(services, failures);
        Collect<UserIdOptions>(services, failures);
        Collect<FirewallOptions>(services, failures);

        if (failures.Count == 0)
        {
            return true;
        }

        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PanRaProxy.Startup");
        Log.InvalidConfiguration(logger, ProxyPaths.SiteSettingsFile, string.Join(Environment.NewLine, failures.Select(f => $"- {f}")));
        return false;
    }

    private static void Collect<T>(IServiceProvider services, List<string> failures)
        where T : class
    {
        try
        {
            _ = services.GetRequiredService<IOptions<T>>().Value;
        }
        catch (OptionsValidationException ex)
        {
            failures.AddRange(ex.Failures);
        }
    }
}
