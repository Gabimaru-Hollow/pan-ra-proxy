using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

public static class MappingRegistration
{
    public static IServiceCollection AddMappingDecision(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<INameTranslator>(sp =>
        {
            DomainOptions domain = sp.GetRequiredService<IOptions<UserIdOptions>>().Value.Domain;

            if (!domain.Rules.Any(r => r.Lookup is not null) || !OperatingSystem.IsWindows())
            {
                return new NoNameTranslation();
            }

            return new CachingNameTranslator(
                ActivatorUtilities.CreateInstance<WindowsNameTranslator>(sp),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMinutes(domain.LookupCacheMinutes));
        });

        services.AddSingleton<MappingDecider>();
        services.AddSingleton<MappingBatcher>();
        services.AddSingleton<IAccountingRequestSink, MappingDecisionSink>();

        return services;
    }
}

/// <summary>
/// Runs the Mapping decision for each accepted request and queues the resulting changes for batching.
/// </summary>
internal sealed class MappingDecisionSink(MappingDecider decider, MappingBatcher batcher, ProxyMetrics metrics, ILogger<MappingDecisionSink> logger) : IAccountingRequestSink
{
    public void Accept(AccountingRequest request)
    {
        switch (decider.Decide(request))
        {
            case MappingDecision.Changes changes:
                foreach (MappingChange change in changes.Items)
                {
                    batcher.Enqueue(change);
                }

                break;

            case MappingDecision.Dropped dropped:
                metrics.RequestDropped(dropped.Reason);

                switch (dropped.Reason)
                {
                    case DropReason.MissingAttributes:
                        Log.MissingAttributes(logger, request);
                        break;

                    case DropReason.Filtered:
                        Log.UsernameFiltered(logger, request);
                        break;

                    default:
                        Log.RequestDropped(logger, request, dropped.Reason);
                        break;
                }

                break;
        }
    }
}
