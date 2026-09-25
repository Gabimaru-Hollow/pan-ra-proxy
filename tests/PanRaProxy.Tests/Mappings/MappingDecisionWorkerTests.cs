using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Mappings;

/// <summary>
/// The listener hands accepted requests to a queue; the Mapping decision, and the directory lookup
/// inside it, runs on its own worker, so a slow domain controller never holds up RADIUS reception.
/// </summary>
public sealed class MappingDecisionWorkerTests : IDisposable
{
    private readonly UserIdOptions options = new()
    {
        BatchWindowMs = 0,
        Domain = new DomainOptions
        {
            Rules = [new DomainRuleOptions { Match = @"^(?<user>[^@\\]+)$", Lookup = "${user}@xdomain.local", Nt4Domain = "XDOMAIN" }],
        },
    };

    private readonly GatedDirectory directory = new();
    private readonly AccountingRequestQueue queue;
    private readonly MappingBatcher batcher;
    private readonly TestMetrics metrics;
    private readonly MappingDecisionWorker worker;

    public MappingDecisionWorkerTests()
    {
        IOptions<UserIdOptions> wrapped = Microsoft.Extensions.Options.Options.Create(this.options);
        this.queue = new AccountingRequestQueue(wrapped);
        this.batcher = new MappingBatcher(wrapped, TimeProvider.System);
        this.metrics = new TestMetrics(this.batcher, this.queue);
        MappingDecider decider = new(wrapped, new CanonicalUsernameResolver(CanonicalUsernameRules.Create(this.options), this.directory));
        this.worker = new MappingDecisionWorker(this.queue, decider, this.batcher, this.metrics.Metrics, NullLogger<MappingDecisionWorker>.Instance);
    }

    public void Dispose()
    {
        this.directory.Open();
        this.worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        this.worker.Dispose();
        this.metrics.Dispose();
    }

    [Fact]
    public async Task A_slow_directory_does_not_hold_up_the_listener()
    {
        await this.worker.StartAsync(CancellationToken.None);

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 1; i <= 3; i++)
        {
            this.queue.Accept(Request($"user{i}", $"10.0.0.{i}"));
        }

        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"Accept took {elapsed.Elapsed}");

        this.directory.Open();
        List<MappingChange> changes = [];
        while (changes.Count < 3)
        {
            changes.AddRange((await this.ReadBatchAsync()).Changes);
        }

        Assert.Equal([@"XDOMAIN\user1.dir", @"XDOMAIN\user2.dir", @"XDOMAIN\user3.dir"], changes.Select(c => c.Mapping.Username));
    }

    [Fact]
    public async Task A_decision_that_throws_does_not_stop_the_worker()
    {
        this.directory.Open();
        this.directory.ThrowFor = "anna@xdomain.local";
        await this.worker.StartAsync(CancellationToken.None);

        this.queue.Accept(Request("anna", "10.0.0.1"));
        this.queue.Accept(Request("bruno", "10.0.0.2"));

        Batch batch = await this.ReadBatchAsync();

        Assert.Equal(@"XDOMAIN\bruno.dir", Assert.Single(batch.Changes).Mapping.Username);
    }

    private async Task<Batch> ReadBatchAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        return await this.batcher.ReadBatchAsync(timeout.Token);
    }

    private static AccountingRequest Request(string user, string ip)
    {
        byte[] status = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(status, AcctStatusType.Start);

        return new AccountingRequest(IPAddress.Parse("192.168.4.127"), 1,
        [
            new(RadiusAttributeType.AcctStatusType, status),
            new(RadiusAttributeType.UserName, Encoding.UTF8.GetBytes(user)),
            new(RadiusAttributeType.FramedIpAddress, IPAddress.Parse(ip).GetAddressBytes()),
        ]);
    }

    /// <summary>
    /// A directory that answers only once opened, the way a domain controller does after an outage.
    /// </summary>
    private sealed class GatedDirectory : INameTranslator
    {
        private readonly ManualResetEventSlim gate = new();

        public string? ThrowFor { get; set; }

        public void Open() => this.gate.Set();

        public string? TryTranslateToNt4(string upn)
        {
            this.gate.Wait(TimeSpan.FromSeconds(10));

            if (upn == this.ThrowFor)
            {
                throw new InvalidOperationException("directory failure");
            }

            return $"XDOMAIN\\{upn[..upn.IndexOf('@')]}.dir";
        }
    }
}
