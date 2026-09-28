using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;

namespace PanRaProxy.Options;

public static class StartupValidation
{
    /// <summary>
    /// Validates every options section and logs all failures together (event 3106), so an administrator
    /// sees the whole list instead of the first problem only. False means the Proxy must not start.
    /// </summary>
    /// <remarks>
    /// With valid options it then builds the modules, so what only construction finds out (a RADIUS Client
    /// that doesn't resolve, a CaFile that holds no certificate) is reported the same way. Construction
    /// stops at the first such problem.
    /// </remarks>
    public static bool Validate(IServiceProvider services)
    {
        List<string> failures = [];

        Collect<RadiusOptions>(services, failures);
        Collect<UserIdOptions>(services, failures);
        Collect<FirewallOptions>(services, failures);

        if (failures.Count == 0)
        {
            try
            {
                _ = services.GetServices<IHostedService>().ToList();
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex.Message);
            }
        }

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
        catch (Exception ex)
        {
            // Binding fails before validation runs, e.g. Radius:Port=abc: "Failed to convert configuration value ...".
            failures.Add(ex.Message);
        }
    }
}
