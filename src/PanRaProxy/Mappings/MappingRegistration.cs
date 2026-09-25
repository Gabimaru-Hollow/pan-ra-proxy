using Microsoft.Extensions.Options;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

internal static class MappingRegistration
{
    public static IServiceCollection AddMappingDecision(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<UserIdOptions>, CanonicalUsernameRulesValidator>();
        services.AddSingleton(sp => CanonicalUsernameRules.Create(sp.GetRequiredService<IOptions<UserIdOptions>>().Value));

        services.AddSingleton<INameTranslator>(sp =>
        {
            // The validator refuses Lookup on anything but Windows; the check is repeated for the platform analyzer.
            if (!sp.GetRequiredService<CanonicalUsernameRules>().UseLookup || !OperatingSystem.IsWindows())
            {
                return new NoNameTranslation();
            }

            return new CachingNameTranslator(
                ActivatorUtilities.CreateInstance<WindowsNameTranslator>(sp),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMinutes(sp.GetRequiredService<IOptions<UserIdOptions>>().Value.Domain.LookupCacheMinutes));
        });

        services.AddSingleton<CanonicalUsernameResolver>();
        services.AddSingleton<MappingDecider>();
        services.AddSingleton<MappingBatcher>();
        services.AddSingleton<AccountingRequestQueue>();
        services.AddSingleton<IAccountingRequestSink>(sp => sp.GetRequiredService<AccountingRequestQueue>());
        services.AddHostedService<MappingDecisionWorker>();

        return services;
    }
}
