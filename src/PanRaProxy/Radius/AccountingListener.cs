using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;

namespace PanRaProxy.Radius;

/// <summary>
/// Receives an accepted <see cref="AccountingRequest"/>. Must not block: the listener calls it inline.
/// </summary>
public interface IAccountingRequestSink
{
    void Accept(AccountingRequest request);
}

/// <summary>
/// Terminates the process. Production calls <see cref="Environment.Exit(int)"/> so Windows Service
/// recovery restarts the Proxy (NFR-08); tests record the call instead.
/// </summary>
public delegate void ProcessExit(int exitCode);

/// <summary>
/// UDP adapter around <see cref="AccountingPacketHandler"/>. One bad datagram, or one failing sink call,
/// never stops the loop. A failure of the socket itself exits the process.
/// </summary>
public sealed class AccountingListener(
    IOptions<RadiusOptions> options,
    AccountingPacketHandler handler,
    IAccountingRequestSink sink,
    ProxyMetrics metrics,
    ProcessExit exit,
    ILogger<AccountingListener> logger) : BackgroundService
{
    // Windows reports an ICMP port-unreachable for an earlier send as ConnectionReset on the next receive.
    private const int SioUdpConnReset = -1744830452;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using UdpClient udp = new(new IPEndPoint(IPAddress.Any, options.Value.Port));

            if (OperatingSystem.IsWindows())
            {
                udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            Log.Listening(logger, options.Value.Port);

            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;

                try
                {
                    received = await udp.ReceiveAsync(stoppingToken);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }

                await this.ProcessAsync(udp, received, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.ListenerFailed(logger, ex);
            exit(1);
        }
    }

    private async Task ProcessAsync(UdpClient udp, UdpReceiveResult received, CancellationToken stoppingToken)
    {
        IPEndPoint remote = received.RemoteEndPoint;
        metrics.PacketReceived();

        try
        {
            switch (handler.Handle(received.Buffer, remote.Address))
            {
                case PacketVerdict.Accepted accepted:
                    // Acknowledge before handing on: the ACK means "received", not "Mapping made" (FR-00).
                    await udp.SendAsync(accepted.Response, remote, stoppingToken);
                    metrics.RequestAccepted(accepted.Request);
                    Log.RequestAccepted(logger, accepted.Request);

                    try
                    {
                        sink.Accept(accepted.Request);
                    }
                    catch (Exception ex)
                    {
                        Log.RequestProcessingFailed(logger, ex, accepted.Request);
                    }

                    break;

                case PacketVerdict.Discarded discarded:
                    metrics.PacketDiscarded(discarded.Reason);

                    if (discarded.Reason == DiscardReason.UnknownClient)
                    {
                        Log.UnknownRadiusClient(logger, remote.Address);
                    }
                    else
                    {
                        Log.PacketDiscarded(logger, remote.Address, discarded.Reason);
                    }

                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.DatagramHandlingFailed(logger, ex, remote);
        }
    }
}
