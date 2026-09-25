using System.Net;
using Microsoft.Extensions.Options;
using PanRaProxy.Diagnostics;
using PanRaProxy.Mappings;
using PanRaProxy.Options;

namespace PanRaProxy.Firewall;

/// <summary>
/// Sends a Batch to the Firewalls and reports what each Login and Logout became.
/// Hides the uid-message XML, the X-PAN-KEY header, the per-attempt timeout, TLS trust (configured
/// on the HttpClient's handler) and ordered failover. Failover happens only when a Firewall can't be
/// reached (connection or TLS failure, timeout, HTTP 5xx); the last Firewall that answered is tried
/// first next time.
/// </summary>
public sealed class FirewallClient
{
    private readonly HttpClient http;
    private readonly IReadOnlyList<Uri> firewalls;
    private readonly string apiKey;
    private readonly TimeSpan attemptTimeout;
    private readonly TimeProvider time;
    private readonly ILogger<FirewallClient> logger;
    private int active;

    public FirewallClient(
        HttpClient http,
        IOptions<FirewallOptions> options,
        SecretLookup secrets,
        TimeProvider time,
        ILogger<FirewallClient> logger)
    {
        this.http = http;
        this.firewalls = options.Value.Endpoints;
        this.apiKey = secrets(options.Value.ApiKeySecretName)
                      ?? throw new InvalidOperationException($"Secret '{options.Value.ApiKeySecretName}' is not set.");
        this.attemptTimeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
        this.time = time;
        this.logger = logger;

        // The per-attempt timeout below is the only one that applies.
        this.http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<SubmissionResult> SubmitAsync(Batch batch, CancellationToken cancellationToken)
    {
        string command = UidMessage.Serialize(batch);
        List<FailedAttempt> attempts = [];
        int start = Volatile.Read(ref this.active);

        for (int i = 0; i < this.firewalls.Count; i++)
        {
            int index = (start + i) % this.firewalls.Count;
            Uri firewall = this.firewalls[index];

            (SubmissionResult? result, string? unreachable) = await this.TryFirewallAsync(firewall, command, cancellationToken);

            if (result is not null)
            {
                if (index != start)
                {
                    Volatile.Write(ref this.active, index);
                    Log.FailedOver(this.logger, firewall);
                }

                return result;
            }

            attempts.Add(new FailedAttempt(firewall, unreachable!));
            Log.FirewallUnreachable(this.logger, firewall, unreachable!);
        }

        return new SubmissionResult.Unreachable(attempts);
    }

    private async Task<(SubmissionResult? Result, string? Unreachable)> TryFirewallAsync(Uri firewall, string command, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(this.attemptTimeout, this.time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        using HttpRequestMessage request = new(HttpMethod.Post, firewall)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("type", "user-id"),
                new KeyValuePair<string, string>("cmd", command),
            ]),
        };
        request.Headers.Add("X-PAN-KEY", this.apiKey);

        try
        {
            using HttpResponseMessage response = await this.http.SendAsync(request, linked.Token);

            if ((int)response.StatusCode >= 500)
            {
                return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            string body = await response.Content.ReadAsStringAsync(linked.Token);
            string? refused = UidMessage.ParseResponse(body, out List<Rejection> rejections);

            if (refused is null && response.StatusCode != HttpStatusCode.OK)
            {
                refused = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }

            return refused is null
                ? (new SubmissionResult.Accepted(firewall, rejections), null)
                : (new SubmissionResult.ApiError(firewall, refused), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"no response within {this.attemptTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
    }
}
