using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

/// <summary>
/// Where the listener leaves accepted requests for the Mapping decision. It never blocks: the decision
/// can wait on the directory (Name Translation), and RADIUS reception must not wait with it. Bounded like
/// the Batch queue (NFR-03): at UserId:QueueCapacity the oldest request is dropped and counted.
/// </summary>
public sealed class AccountingRequestQueue : IAccountingRequestSink
{
    private readonly Channel<AccountingRequest> queue;
    private long dropped;

    public AccountingRequestQueue(IOptions<UserIdOptions> options)
    {
        this.queue = Channel.CreateBounded<AccountingRequest>(
            new BoundedChannelOptions(options.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref this.dropped));
    }

    /// <summary>
    /// Requests discarded because the queue was full.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref this.dropped);

    public int QueueLength => this.queue.Reader.Count;

    internal ChannelReader<AccountingRequest> Reader => this.queue.Reader;

    public void Accept(AccountingRequest request) => this.queue.Writer.TryWrite(request);
}

/// <summary>
/// Runs the Mapping decision for each queued request and hands the resulting changes to the Batch.
/// One request that fails is logged and skipped; nothing stops the loop except shutdown.
/// </summary>
internal sealed class MappingDecisionWorker(
    AccountingRequestQueue requests,
    MappingDecider decider,
    MappingBatcher batcher,
    ProxyMetrics metrics,
    ILogger<MappingDecisionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (AccountingRequest request in requests.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    this.Decide(request);
                }
                catch (Exception ex)
                {
                    Log.RequestProcessingFailed(logger, ex, request);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Decide(AccountingRequest request)
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
