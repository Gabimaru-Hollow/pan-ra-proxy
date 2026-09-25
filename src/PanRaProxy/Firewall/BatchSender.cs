using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Mappings;
using PanRaProxy.Options;

namespace PanRaProxy.Firewall;

/// <summary>
/// Reads Batches and submits them to the Firewall, one at a time. A failing Batch is logged and
/// dropped (P3-4); nothing stops the loop except shutdown.
/// </summary>
internal sealed class BatchSender(
    MappingBatcher batcher,
    FirewallClient firewall,
    ProxyMetrics metrics,
    IOptions<FirewallOptions> options,
    ILogger<BatchSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.DisableCertificateValidation)
        {
            Log.CertificateValidationDisabled(logger);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Batch batch;
            try
            {
                batch = await batcher.ReadBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                SubmissionResult result = await firewall.SubmitAsync(batch, stoppingToken);
                metrics.BatchSubmitted(batch, result);
                this.Report(batch, result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                metrics.BatchFailed();
                Log.BatchSubmissionFailed(logger, ex, batch.Changes.Count);
            }
        }
    }

    private void Report(Batch batch, SubmissionResult result)
    {
        int logins = batch.Logins.Count();
        int logouts = batch.Logouts.Count();

        switch (result)
        {
            case SubmissionResult.Accepted accepted:
                Log.BatchApplied(logger, accepted.Firewall, logins, logouts, accepted.Rejections.Count);

                foreach (Rejection rejection in accepted.Rejections)
                {
                    if (rejection.Kind == RejectionKind.Login)
                    {
                        Log.LoginRejected(logger, rejection.Username, rejection.IpAddress, rejection.Message);
                    }
                    else
                    {
                        Log.LogoutRejected(logger, rejection.Username, rejection.IpAddress, rejection.Message);
                    }
                }

                break;

            case SubmissionResult.ApiError error:
                Log.BatchRefused(logger, error.Firewall, logins, logouts, error.Message);
                break;

            case SubmissionResult.Unreachable unreachable:
                Log.NoFirewallReachable(logger, logins, logouts, string.Join("; ", unreachable.Attempts.Select(a => $"{a.Firewall}: {a.Reason}")));
                break;
        }
    }
}
