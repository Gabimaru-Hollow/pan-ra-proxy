using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Mappings;

/// <summary>
/// One call's worth of Logins and Logouts: at most one Login per IP, and no Logout for an IP that a Login in
/// the same Batch gives to someone else. What the Firewall ends up with doesn't depend on the order of the
/// entries, which the uid-message doesn't keep (every Login is listed before every Logout).
/// </summary>
public sealed record Batch(IReadOnlyList<MappingChange> Changes)
{
    public IEnumerable<MappingChange.Login> Logins => this.Changes.OfType<MappingChange.Login>();

    public IEnumerable<MappingChange.Logout> Logouts => this.Changes.OfType<MappingChange.Logout>();
}

/// <summary>
/// Groups Logins and Logouts into Batches (ADR 0001). The queue is bounded and drops the oldest change
/// when full (NFR-03). A Batch closes when it holds BatchSize Mappings or when BatchWindowMs has passed
/// since its first change was queued: a hard timer that later changes don't restart. Within a Batch the latest
/// change for each IP wins, because an IP belongs to at most one Mapping.
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

        // A Batch holds at most BatchSize changes, so a linear search is cheaper than keeping an index in step.
        void Add(MappingChange change)
        {
            IPAddress ip = change.Mapping.IpAddress;

            if (change is MappingChange.Login)
            {
                // A Login gives the IP to its user on the Firewall: every earlier change for that IP is moot.
                changes.RemoveAll(c => c.Mapping.IpAddress.Equals(ip));
                changes.Add(change);
            }
            else if (!changes.Any(c => c is MappingChange.Login && c.Mapping.IpAddress.Equals(ip) && c.Mapping != change.Mapping))
            {
                // A Logout stands only while no one else's Login claims the IP: sent after that Login, it
                // could take the IP from its new holder.
                changes.RemoveAll(c => c.Mapping == change.Mapping);
                changes.Add(change);
            }
        }
    }

    private readonly record struct Queued(MappingChange Change, long Timestamp);
}
