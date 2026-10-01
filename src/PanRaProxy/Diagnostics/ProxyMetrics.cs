using System.Diagnostics.Metrics;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Radius;

namespace PanRaProxy.Diagnostics;

/// <summary>
/// The Proxy's metrics (NFR-04), on the <c>PanRaProxy</c> meter. Read them live with
/// <c>dotnet-counters monitor -n PanRaProxy --counters PanRaProxy</c>, or export them with OpenTelemetry.
/// </summary>
public sealed class ProxyMetrics
{
    public const string MeterName = "PanRaProxy";

    private readonly Counter<long> packetsReceived;
    private readonly Counter<long> packetsDiscarded;
    private readonly Counter<long> requestsAccepted;
    private readonly Counter<long> requestsDropped;
    private readonly Counter<long> changesSent;
    private readonly Counter<long> changesRejected;
    private readonly Counter<long> batches;

    public ProxyMetrics(IMeterFactory meterFactory, MappingBatcher batcher, AccountingRequestQueue requests)
    {
        Meter meter = meterFactory.Create(MeterName);

        this.packetsReceived = meter.CreateCounter<long>("panraproxy.radius.packets.received", "{packet}", "Datagrams received on the accounting port");
        this.packetsDiscarded = meter.CreateCounter<long>("panraproxy.radius.packets.discarded", "{packet}", "Datagrams discarded without a response, by reason");
        this.requestsAccepted = meter.CreateCounter<long>("panraproxy.radius.requests.accepted", "{request}", "Authenticated Accounting-Requests, by Acct-Status-Type");
        this.requestsDropped = meter.CreateCounter<long>("panraproxy.mappings.requests.dropped", "{request}", "Accepted requests that produced no Login or Logout, by reason");
        this.changesSent = meter.CreateCounter<long>("panraproxy.firewall.changes.sent", "{change}", "Logins and Logouts in Batches a Firewall processed");
        this.changesRejected = meter.CreateCounter<long>("panraproxy.firewall.changes.rejected", "{change}", "Logins and Logouts a Firewall rejected");
        this.batches = meter.CreateCounter<long>("panraproxy.firewall.batches", "{batch}", "Batches submitted, by result");

        meter.CreateObservableGauge("panraproxy.requests.queue.depth", () => requests.QueueLength, "{request}", "Accepted requests waiting for the Mapping decision");
        meter.CreateObservableCounter("panraproxy.requests.queue.dropped", () => requests.DroppedCount, "{request}", "Accepted requests dropped because the queue was full");
        meter.CreateObservableGauge("panraproxy.queue.depth", () => batcher.QueueLength, "{change}", "Logins and Logouts waiting to be batched");
        meter.CreateObservableCounter("panraproxy.queue.dropped", () => batcher.DroppedCount, "{change}", "Logins and Logouts dropped because the queue was full");
    }

    public void PacketReceived() => this.packetsReceived.Add(1);

    public void PacketDiscarded(DiscardReason reason) => this.packetsDiscarded.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void RequestAccepted(AccountingRequest request) =>
        this.requestsAccepted.Add(1, new KeyValuePair<string, object?>("status", request.StatusType switch
        {
            AcctStatusType.Start => "start",
            AcctStatusType.InterimUpdate => "interim-update",
            AcctStatusType.Stop => "stop",
            _ => "other",
        }));

    public void RequestDropped(DropReason reason) => this.requestsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void BatchSubmitted(Batch batch, SubmissionResult result)
    {
        string outcome = result switch
        {
            SubmissionResult.Accepted => "accepted",
            SubmissionResult.ApiError => "api-error",
            SubmissionResult.Unreachable => "unreachable",
            SubmissionResult.NotSent => "not-sent",
            _ => "unknown",
        };

        this.batches.Add(1, new KeyValuePair<string, object?>("result", outcome));

        if (result is not SubmissionResult.Accepted accepted)
        {
            return;
        }

        this.changesSent.Add(batch.Logins.Count(), new KeyValuePair<string, object?>("kind", "login"));
        this.changesSent.Add(batch.Logouts.Count(), new KeyValuePair<string, object?>("kind", "logout"));

        foreach (Rejection rejection in accepted.Rejections)
        {
            this.changesRejected.Add(1, new KeyValuePair<string, object?>("kind", rejection.Kind == RejectionKind.Login ? "login" : "logout"));
        }
    }

    public void BatchFailed() => this.batches.Add(1, new KeyValuePair<string, object?>("result", "exception"));
}
