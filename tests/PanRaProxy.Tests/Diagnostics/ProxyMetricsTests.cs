using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;
using PanRaProxy.Tests.Radius;

namespace PanRaProxy.Tests.Diagnostics;

public class ProxyMetricsTests
{
    private static long Sum(MetricCollector<long> collector, string? tag = null, string? value = null) =>
        collector.GetMeasurementSnapshot()
            .Where(m => tag is null || Equals(m.Tags[tag], value))
            .Sum(m => m.Value);

    [Fact]
    public async Task Listener_counts_received_accepted_and_discarded_packets()
    {
        using TestMetrics metrics = new();
        using MetricCollector<long> received = metrics.Collect("panraproxy.radius.packets.received");
        using MetricCollector<long> accepted = metrics.Collect("panraproxy.radius.requests.accepted");
        using MetricCollector<long> discarded = metrics.Collect("panraproxy.radius.packets.discarded");

        int port;
        using (UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        using AccountingListener listener = new(
            Microsoft.Extensions.Options.Options.Create(new RadiusOptions { Port = port }),
            new AccountingPacketHandler(RadiusFixtures.Registry()),
            new NullSink(),
            metrics.Metrics,
            _ => { },
            NullLogger<AccountingListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        using UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint target = new(IPAddress.Loopback, port);
        await client.SendAsync(RadiusFixtures.Get("wrong-secret").RequestBytes, target);
        await client.SendAsync(RadiusFixtures.Get("start").RequestBytes, target);
        await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await client.SendAsync(RadiusFixtures.Get("stop").RequestBytes, target);
        await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await listener.StopAsync(CancellationToken.None);

        Assert.Equal(3, Sum(received));
        Assert.Equal(1, Sum(accepted, "status", "start"));
        Assert.Equal(1, Sum(accepted, "status", "stop"));
        Assert.Equal(1, Sum(discarded, "reason", nameof(DiscardReason.BadAuthenticator)));
    }

    [Fact]
    public void Submitted_batch_counts_sent_and_rejected_changes_by_kind()
    {
        using TestMetrics metrics = new();
        using MetricCollector<long> sent = metrics.Collect("panraproxy.firewall.changes.sent");
        using MetricCollector<long> rejected = metrics.Collect("panraproxy.firewall.changes.rejected");
        using MetricCollector<long> batches = metrics.Collect("panraproxy.firewall.batches");

        Batch batch = new(
        [
            new MappingChange.Login(new Mapping("CONTOSO\\a", IPAddress.Parse("10.0.0.1")), TimeSpan.FromMinutes(75)),
            new MappingChange.Login(new Mapping("CONTOSO\\b", IPAddress.Parse("10.0.0.2")), TimeSpan.FromMinutes(75)),
            new MappingChange.Logout(new Mapping("CONTOSO\\c", IPAddress.Parse("10.0.0.3"))),
        ]);
        Uri firewall = new("https://fw-a.test/api/");

        metrics.Metrics.BatchSubmitted(batch, new SubmissionResult.Accepted(firewall, [new Rejection(RejectionKind.Login, "CONTOSO\\b", "10.0.0.2", "Invalid user name")]));
        metrics.Metrics.BatchSubmitted(batch, new SubmissionResult.Unreachable([new FailedAttempt(firewall, "refused")]));
        metrics.Metrics.BatchFailed();

        Assert.Equal(2, Sum(sent, "kind", "login"));
        Assert.Equal(1, Sum(sent, "kind", "logout"));
        Assert.Equal(1, Sum(rejected, "kind", "login"));
        Assert.Equal(1, Sum(batches, "result", "accepted"));
        Assert.Equal(1, Sum(batches, "result", "unreachable"));
        Assert.Equal(1, Sum(batches, "result", "exception"));
    }

    [Fact]
    public void Queue_depth_and_dropped_changes_are_observable()
    {
        MappingBatcher batcher = new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions { BatchSize = 1, QueueCapacity = 3 }), TimeProvider.System);
        using TestMetrics metrics = new(batcher);
        using MetricCollector<int> depth = metrics.CollectInt("panraproxy.queue.depth");
        using MetricCollector<long> dropped = metrics.Collect("panraproxy.queue.dropped");

        for (int i = 1; i <= 5; i++)
        {
            batcher.Enqueue(new MappingChange.Login(new Mapping($"CONTOSO\\u{i}", IPAddress.Parse($"10.0.0.{i}")), TimeSpan.FromMinutes(75)));
        }

        depth.RecordObservableInstruments();
        dropped.RecordObservableInstruments();

        Assert.Equal(3, depth.LastMeasurement?.Value);
        Assert.Equal(2, dropped.LastMeasurement?.Value);
    }

    [Fact]
    public void Requests_waiting_for_the_mapping_decision_are_observable()
    {
        AccountingRequestQueue requests = new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions { BatchSize = 1, QueueCapacity = 3 }));
        using TestMetrics metrics = new(requests: requests);
        using MetricCollector<int> depth = metrics.CollectInt("panraproxy.requests.queue.depth");
        using MetricCollector<long> dropped = metrics.Collect("panraproxy.requests.queue.dropped");

        for (int i = 1; i <= 5; i++)
        {
            requests.Accept(new AccountingRequest(IPAddress.Parse($"10.0.0.{i}"), (byte)i, []));
        }

        depth.RecordObservableInstruments();
        dropped.RecordObservableInstruments();

        Assert.Equal(3, depth.LastMeasurement?.Value);
        Assert.Equal(2, dropped.LastMeasurement?.Value);
    }

    [Fact]
    public void Mapping_drops_are_counted_by_reason()
    {
        using TestMetrics metrics = new();
        using MetricCollector<long> dropped = metrics.Collect("panraproxy.mappings.requests.dropped");

        metrics.Metrics.RequestDropped(DropReason.Filtered);
        metrics.Metrics.RequestDropped(DropReason.Filtered);
        metrics.Metrics.RequestDropped(DropReason.NoUsableIpAddress);

        Assert.Equal(2, Sum(dropped, "reason", nameof(DropReason.Filtered)));
        Assert.Equal(1, Sum(dropped, "reason", nameof(DropReason.NoUsableIpAddress)));
    }

    private sealed class NullSink : IAccountingRequestSink
    {
        public void Accept(AccountingRequest request)
        {
        }
    }
}
