using System.Net;
using Microsoft.Extensions.Time.Testing;
using PanRaProxy.Mappings;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Mappings;

public class MappingBatcherTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(75);

    private readonly FakeTimeProvider time = new();

    private MappingBatcher Batcher(int batchSize = 200, int windowMs = 50, int capacity = 10_000) =>
        new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions { BatchSize = batchSize, BatchWindowMs = windowMs, QueueCapacity = capacity }), this.time);

    /// <summary>
    /// Each user on an IP of their own (a on .1, b on .2, ...) unless a test puts them on the same one.
    /// </summary>
    private static Mapping M(string user, string? ip = null) => new(user, IPAddress.Parse(ip ?? $"10.20.30.{user[0] - 'a' + 1}"));

    private static MappingChange.Login Login(string user, string? ip = null) => new(M(user, ip), Timeout);

    private static MappingChange.Logout Logout(string user, string? ip = null) => new(M(user, ip));

    private const string SharedIp = "10.20.30.99";

    /// <summary>
    /// Lets continuations scheduled by the fake clock run, and proves the task is still pending.
    /// </summary>
    private static async Task AssertPending(Task task)
    {
        await Task.Delay(50);
        Assert.False(task.IsCompleted, "the Batch closed before its window elapsed");
    }

    [Fact]
    public async Task Lone_login_is_released_when_the_window_elapses()
    {
        MappingBatcher batcher = this.Batcher();
        batcher.Enqueue(Login("a"));

        Task<Batch> read = batcher.ReadBatchAsync(CancellationToken.None);
        await AssertPending(read);

        this.time.Advance(TimeSpan.FromMilliseconds(49));
        await AssertPending(read);

        this.time.Advance(TimeSpan.FromMilliseconds(1));
        Batch batch = await read.WaitAsync(Settle);

        Assert.Equal([Login("a")], batch.Changes);
    }

    [Fact]
    public async Task Trickle_does_not_restart_the_window()
    {
        MappingBatcher batcher = this.Batcher();
        batcher.Enqueue(Login("a"));
        Task<Batch> read = batcher.ReadBatchAsync(CancellationToken.None);

        this.time.Advance(TimeSpan.FromMilliseconds(30));
        batcher.Enqueue(Login("b"));
        await AssertPending(read);

        this.time.Advance(TimeSpan.FromMilliseconds(20)); // 50 ms after the first change, 20 after the last
        Batch batch = await read.WaitAsync(Settle);

        Assert.Equal([Login("a"), Login("b")], batch.Changes);
    }

    [Fact]
    public async Task Window_counts_from_when_the_first_change_was_queued()
    {
        MappingBatcher batcher = this.Batcher();
        batcher.Enqueue(Login("a"));

        this.time.Advance(TimeSpan.FromMilliseconds(80)); // the sender was busy; the window has already passed

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Login("a")], batch.Changes);
    }

    [Fact]
    public async Task Full_batch_closes_without_waiting()
    {
        MappingBatcher batcher = this.Batcher(batchSize: 3);
        foreach (string user in new[] { "a", "b", "c", "d" })
        {
            batcher.Enqueue(Login(user));
        }

        Batch first = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);
        Assert.Equal([Login("a"), Login("b"), Login("c")], first.Changes);

        Task<Batch> second = batcher.ReadBatchAsync(CancellationToken.None);
        this.time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal([Login("d")], (await second.WaitAsync(Settle)).Changes);
    }

    [Fact]
    public async Task Last_change_per_mapping_wins()
    {
        MappingBatcher batcher = this.Batcher(windowMs: 0);
        batcher.Enqueue(Login("a"));
        batcher.Enqueue(Logout("b"));
        batcher.Enqueue(Logout("a"));     // Login → Logout: only the Logout
        batcher.Enqueue(Login("b"));      // Logout → Login: only the Login
        batcher.Enqueue(Login("c"));
        batcher.Enqueue(Login("c"));      // duplicate Logins collapse
        batcher.Enqueue(Login("a", "10.20.30.41")); // same user, other IP: another Mapping

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Logout("a"), Login("b"), Login("c"), Login("a", "10.20.30.41")], batch.Changes);
        Assert.Equal([Logout("a")], batch.Logouts);
    }

    [Fact]
    public async Task The_latest_login_for_an_ip_wins_whatever_came_before()
    {
        // An IP belongs to at most one Mapping: the Batch must not depend on the order of its entries.
        MappingBatcher batcher = this.Batcher(windowMs: 0);
        batcher.Enqueue(Login("a", SharedIp));
        batcher.Enqueue(Login("b", SharedIp));
        batcher.Enqueue(Login("a", SharedIp)); // a is back: the IP is a's, not b's

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Login("a", SharedIp)], batch.Changes);
    }

    [Fact]
    public async Task A_login_replaces_an_earlier_logout_for_the_same_ip()
    {
        MappingBatcher batcher = this.Batcher(windowMs: 0);
        batcher.Enqueue(Logout("a", SharedIp));
        batcher.Enqueue(Login("b", SharedIp));

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Login("b", SharedIp)], batch.Changes);
    }

    [Fact]
    public async Task A_logout_of_a_user_who_no_longer_holds_the_ip_is_dropped()
    {
        // uid-messages list every Login before every Logout: sent, this Logout would follow b's Login.
        MappingBatcher batcher = this.Batcher(windowMs: 0);
        batcher.Enqueue(Login("b", SharedIp));
        batcher.Enqueue(Logout("a", SharedIp));

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Login("b", SharedIp)], batch.Changes);
    }

    [Fact]
    public async Task A_shared_account_keeps_one_login_per_ip()
    {
        MappingBatcher batcher = this.Batcher(windowMs: 0);
        batcher.Enqueue(Login("s", "10.20.30.71"));
        batcher.Enqueue(Login("s", "10.20.30.72"));
        batcher.Enqueue(Logout("s", "10.20.30.71")); // one device leaves; the other stays
        batcher.Enqueue(Login("s", "10.20.30.73"));

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);

        Assert.Equal([Login("s", "10.20.30.72"), Logout("s", "10.20.30.71"), Login("s", "10.20.30.73")], batch.Changes);
    }

    [Fact]
    public async Task Full_queue_drops_the_oldest_changes_and_counts_them()
    {
        MappingBatcher batcher = this.Batcher(windowMs: 0, capacity: 3);
        foreach (string user in new[] { "a", "b", "c", "d", "e" })
        {
            batcher.Enqueue(Login(user));
        }

        Assert.Equal(2, batcher.DroppedCount);
        Assert.Equal(3, batcher.QueueLength);

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None).WaitAsync(Settle);
        Assert.Equal([Login("c"), Login("d"), Login("e")], batch.Changes);
    }

    [Fact]
    public async Task Waiting_for_the_first_change_is_cancellable()
    {
        using CancellationTokenSource stop = new();
        Task<Batch> read = this.Batcher().ReadBatchAsync(stop.Token);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Settle));
    }

    [Fact]
    public async Task Cancelling_during_the_window_throws_rather_than_returning_a_partial_batch()
    {
        MappingBatcher batcher = this.Batcher();
        batcher.Enqueue(Login("a"));
        using CancellationTokenSource stop = new();
        Task<Batch> read = batcher.ReadBatchAsync(stop.Token);
        await AssertPending(read);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Settle));
    }
}
