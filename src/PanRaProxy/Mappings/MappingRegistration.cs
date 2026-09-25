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
            sp.GetRequiredService<IOptions<UserIdOptions>>().Value.NameTranslation && OperatingSystem.IsWindows()
                ? ActivatorUtilities.CreateInstance<WindowsNameTranslator>(sp)
                : new NoNameTranslation());

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
