using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Mappings;

/// <summary>
/// One call's worth of Logins and Logouts, at most one per Mapping.
/// </summary>
public sealed record Batch(IReadOnlyList<MappingChange> Changes)
{
    public IEnumerable<MappingChange.Login> Logins => this.Changes.OfType<MappingChange.Login>();

    public IEnumerable<MappingChange.Logout> Logouts => this.Changes.OfType<MappingChange.Logout>();
}

/// <summary>
/// Groups Logins and Logouts into Batches (ADR 0001). The queue is bounded and drops the oldest change
/// when full (NFR-03). A Batch closes when it holds BatchSize Mappings or when BatchWindowMs has passed
/// since its first change was queued: a hard timer that later changes don't restart. Within a Batch the last
/// change per Mapping wins.
/// </summary>
public sealed class MappingBatcher
{
    private readonly Channel<Queued> queue;
    private readonly TimeProvider time;
    private readonly int batchSize;
    private readonly TimeSpan window;
    private long dropped;

    public MappingBatcher(IOptions<UserIdOptions> options, TimeProvider time)
    {
        this.time = time;
        this.batchSize = options.Value.BatchSize;
        this.window = TimeSpan.FromMilliseconds(options.Value.BatchWindowMs);
        this.queue = Channel.CreateBounded<Queued>(
            new BoundedChannelOptions(options.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref this.dropped));
    }

    /// <summary>
    /// Changes discarded because the queue was full.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref this.dropped);

    public int QueueLength => this.queue.Reader.Count;

    /// <summary>
    /// Never blocks: when the queue is full the oldest change is dropped.
    /// </summary>
    public void Enqueue(MappingChange change) => this.queue.Writer.TryWrite(new Queued(change, this.time.GetTimestamp()));

    /// <summary>
    /// Waits for the first change, then collects until the Batch is full or the window has passed.
    /// </summary>
    public async Task<Batch> ReadBatchAsync(CancellationToken cancellationToken)
    {
        ChannelReader<Queued> reader = this.queue.Reader;
        Dictionary<Mapping, int> positions = [];
        List<MappingChange> changes = [];

        Queued first = await reader.ReadAsync(cancellationToken);
        Add(first.Change);

        // Once the window has passed, take only what is already queued.
        TimeSpan remaining = this.window - this.time.GetElapsedTime(first.Timestamp);
        using CancellationTokenSource windowTimer = new(remaining > TimeSpan.Zero ? remaining : Timeout.InfiniteTimeSpan, this.time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowTimer.Token);

        while (changes.Count < this.batchSize)
        {
            if (reader.TryRead(out Queued next))
            {
                Add(next.Change);
                continue;
            }

            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                if (!await reader.WaitToReadAsync(linked.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // window elapsed
            }
        }

        return new Batch(changes);

        void Add(MappingChange change)
        {
            if (positions.TryGetValue(change.Mapping, out int index))
            {
                changes[index] = change;
            }
            else
            {
                positions[change.Mapping] = changes.Count;
                changes.Add(change);
            }
        }
    }

    private readonly record struct Queued(MappingChange Change, long Timestamp);
}
