using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PanRaProxy.Options;

internal static class OptionsRegistration
{
    /// <summary>
    /// Binds and validates every options section. Validation runs at host start, so a bad
    /// regex, a missing secret or an empty Firewall list stops the Proxy before it listens.
    /// </summary>
    public static IServiceCollection AddProxyOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RadiusOptions>().Bind(configuration.GetSection(RadiusOptions.SectionName)).ValidateOnStart();
        services.AddOptions<UserIdOptions>().Bind(configuration.GetSection(UserIdOptions.SectionName)).ValidateOnStart();
        services.AddOptions<FirewallOptions>().Bind(configuration.GetSection(FirewallOptions.SectionName)).ValidateOnStart();

        services.AddSingleton<IValidateOptions<RadiusOptions>, RadiusOptionsValidator>();
        services.AddSingleton<IValidateOptions<UserIdOptions>, UserIdOptionsValidator>();
        services.AddSingleton<IValidateOptions<FirewallOptions>, FirewallOptionsValidator>();

        return services;
    }
}
