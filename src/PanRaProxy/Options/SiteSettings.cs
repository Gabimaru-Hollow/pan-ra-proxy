using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace PanRaProxy.Options;

public static class SiteSettings
{
    /// <summary>
    /// Adds the site's appsettings.json (outside the install folder, never touched by the MSI) right after the
    /// last JSON file already configured. It overrides the install folder's defaults, while environment
    /// variables and command-line arguments still override it.
    /// </summary>
    public static IConfigurationBuilder AddSiteSettings(this IConfigurationBuilder configuration, string path)
    {
        JsonConfigurationSource site = new() { Path = path, Optional = true, ReloadOnChange = false };
        site.ResolveFileProvider();

        int lastJson = -1;
        for (int i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource)
            {
                lastJson = i;
            }
        }

        configuration.Sources.Insert(lastJson + 1, site);
        return configuration;
    }
}
