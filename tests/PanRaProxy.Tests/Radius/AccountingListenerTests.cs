using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Radius;

public sealed class AccountingListenerTests : IAsyncLifetime
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoReplyWait = TimeSpan.FromMilliseconds(250);

    private readonly RecordingSink sink = new();
    private readonly TestMetrics testMetrics = new();
    private readonly ConcurrentQueue<int> exitCodes = new();
    private readonly UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly int port = FreeUdpPort();
    private AccountingListener listener = null!;

    private ProxyMetrics metrics => this.testMetrics.Metrics;

    public async Task InitializeAsync()
    {
        this.listener = this.CreateListener(this.port);
        await this.listener.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await this.listener.StopAsync(CancellationToken.None);
        this.listener.Dispose();
        this.client.Dispose();
        this.testMetrics.Dispose();
    }

    [Fact]
    public async Task Accepted_packet_is_acknowledged_and_handed_to_the_sink()
    {
        RadiusFixture fixture = RadiusFixtures.Get("start-with-proxy-state");

        byte[]? reply = await this.SendAsync(fixture.RequestBytes);

        Assert.Equal(fixture.ResponseBytes, reply);
        AccountingRequest request = Assert.Single(this.sink.Requests);
        Assert.Equal("CONTOSO\\mrossi", request.UserName);
    }

    [Fact]
    public async Task Discarded_packet_gets_no_reply()
    {
        byte[]? reply = await this.SendAsync(RadiusFixtures.Get("wrong-secret").RequestBytes, expectReply: false);

        Assert.Null(reply);
        Assert.Empty(this.sink.Requests);
    }

    [Fact]
    public async Task Garbage_does_not_stop_the_listener()
    {
        RadiusFixture fixture = RadiusFixtures.Get("start");

        foreach (string name in new[] { "truncated", "shorter-than-header", "attribute-overruns-packet", "short-integer-attribute" })
        {
            await this.SendAsync(RadiusFixtures.Get(name).RequestBytes, expectReply: name == "short-integer-attribute");
        }

        await this.SendAsync([], expectReply: false);

        Assert.Equal(fixture.ResponseBytes, await this.SendAsync(fixture.RequestBytes));
        Assert.Empty(this.exitCodes);
    }

    [Fact]
    public async Task Failing_sink_still_acknowledges_and_does_not_stop_the_listener()
    {
        this.sink.ThrowNext = true;
        RadiusFixture first = RadiusFixtures.Get("start");
        RadiusFixture second = RadiusFixtures.Get("interim-update");

        Assert.Equal(first.ResponseBytes, await this.SendAsync(first.RequestBytes));
        Assert.Equal(second.ResponseBytes, await this.SendAsync(second.RequestBytes));
        Assert.Equal(3u, Assert.Single(this.sink.Requests).StatusType);
        Assert.Empty(this.exitCodes);
    }

    [Fact]
    public async Task Port_already_in_use_exits_the_process_with_code_1()
    {
        using AccountingListener second = this.CreateListener(this.port);

        await second.StartAsync(CancellationToken.None);
        await (second.ExecuteTask ?? Task.CompletedTask).WaitAsync(ReplyTimeout);

        Assert.Equal(1, Assert.Single(this.exitCodes));
    }

    private AccountingListener CreateListener(int listenPort) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new RadiusOptions { Port = listenPort }),
            new AccountingPacketHandler(RadiusFixtures.Registry()),
            this.sink,
            this.metrics,
            code => this.exitCodes.Enqueue(code),
            NullLogger<AccountingListener>.Instance);

    private async Task<byte[]?> SendAsync(byte[] datagram, bool expectReply = true)
    {
        await this.client.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, this.port));

        using CancellationTokenSource timeout = new(expectReply ? ReplyTimeout : NoReplyWait);
        try
        {
            return (await this.client.ReceiveAsync(timeout.Token)).Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static int FreeUdpPort()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private sealed class RecordingSink : IAccountingRequestSink
    {
        public ConcurrentQueue<AccountingRequest> Requests { get; } = new();

        public bool ThrowNext { get; set; }

        public void Accept(AccountingRequest request)
        {
            if (this.ThrowNext)
            {
                this.ThrowNext = false;
                throw new InvalidOperationException("sink failure");
            }

            this.Requests.Enqueue(request);
        }
    }
}
