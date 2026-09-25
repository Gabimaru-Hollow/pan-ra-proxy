using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;
using PanRaProxy.Tests.Radius;

namespace PanRaProxy.Tests;

/// <summary>
/// The whole Proxy in-process: accounting in over UDP, uid-message out to a scripted Firewall.
/// </summary>
public class EndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Accounting_start_reaches_the_firewall_as_a_login_and_stop_does_not()
    {
        List<string> uidMessages = [];
        int port = FreeUdpPort();
        Channel<string> firewallCommands = Channel.CreateUnbounded<string>();

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Radius:Port"] = port.ToString(),
            ["Radius:Clients:0:Host"] = "127.0.0.1",
            ["Radius:Clients:0:SecretName"] = "RADIUS_SECRET",
            ["UserId:UsernameRewrites:0:Match"] = @"^([^@\\]+)@domain\.local$",
            ["UserId:UsernameRewrites:0:Replace"] = @"DOMAIN\$1",
            ["Firewalls:Endpoints:0"] = "https://fw-a.test/api/",
            ["Firewalls:ApiKeySecretName"] = "PAN_API_KEY",
        });
        builder.Logging.ClearProviders();
        CapturingLoggerProvider logs = new();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        // The graph that ships, with only the machine and the Firewall's transport replaced.
        builder.Services.AddPanRaProxy(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton(new SecretLookup(name => name switch
        {
            "RADIUS_SECRET" => RadiusFixtures.Clients.Values.First(),
            "PAN_API_KEY" => "lab-key",
            _ => null,
        })));
        builder.Services.Replace(ServiceDescriptor.Singleton(new ProcessExit(code => throw new InvalidOperationException($"exit {code}"))));
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<FirewallClient>(sp, new HttpClient(new RecordingFirewall(firewallCommands.Writer)))));

        using IHost host = builder.Build();
        Assert.True(StartupValidation.Validate(host.Services));
        await host.StartAsync();

        using UdpClient radius = new(new IPEndPoint(IPAddress.Loopback, 0));
        foreach (string name in new[] { "start", "stop", "start-machine-account", "start-upn" })
        {
            await radius.SendAsync(RadiusFixtures.Get(name).RequestBytes, new IPEndPoint(IPAddress.Loopback, port));
            await radius.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(5));
        uidMessages.Add(await firewallCommands.Reader.ReadAsync(wait.Token));
        XElement message = XElement.Parse(uidMessages[0]);

        Assert.Equal(
            [(@"CONTOSO\mrossi", "10.20.30.40", "15"), (@"DOMAIN\mrossi", "10.20.30.41", "15")],
            message.Descendants("login").Elements("entry")
                .Select(e => (e.Attribute("name")!.Value, e.Attribute("ip")!.Value, e.Attribute("timeout")!.Value)));
        Assert.Empty(message.Descendants("logout").Elements("entry"));

        // The Batch result is logged after the Firewall answers: wait for it before stopping.
        for (int i = 0; i < 100 && !logs.EventIds.Contains(4002); i++)
        {
            await Task.Delay(20);
        }

        await host.StopAsync();

        while (firewallCommands.Reader.TryRead(out string? extra))
        {
            uidMessages.Add(extra);
        }

        output.WriteLine($"uid-messages: {TestArtifacts.WriteUidMessages(nameof(this.Accounting_start_reaches_the_firewall_as_a_login_and_stop_does_not), uidMessages)}");

        // Event IDs (NFR-07): 4001 accepted, 4002 Batch applied, 4003 filtered, 4004 listening, 4005 dropped.
        Assert.Contains(4004, logs.EventIds);
        Assert.Equal(4, logs.EventIds.Count(id => id == 4001));
        Assert.Contains(4002, logs.EventIds);
        Assert.Contains(4003, logs.EventIds);
        Assert.Contains(4005, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, id => id is >= 3000 and < 4000);
    }

    private static int FreeUdpPort()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> eventIds = new();

        public IReadOnlyCollection<int> EventIds => this.eventIds;

        public ILogger CreateLogger(string categoryName) => new Capturing(this.eventIds);

        public void Dispose()
        {
        }

        private sealed class Capturing(System.Collections.Concurrent.ConcurrentQueue<int> eventIds) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                eventIds.Enqueue(eventId.Id);
        }
    }

    private sealed class RecordingFirewall(ChannelWriter<string> commands) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string command = body.Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == "cmd")[1];
            await commands.WriteAsync(WebUtility.UrlDecode(command), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<response status=\"success\"><result/></response>"),
            };
        }
    }
}
