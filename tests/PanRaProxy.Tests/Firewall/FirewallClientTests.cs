using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Firewall;

public class FirewallClientTests
{
    private static readonly Uri FirewallA = new("https://fw-a.test/api/");
    private static readonly Uri FirewallB = new("https://fw-b.test/api/");
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    // Response shapes from PAN-OS User-ID API documentation and upstream's recorded samples.
    // Replace with captures from the lab Firewall when available.
    private const string Success =
        "<response status=\"success\"><result><uid-response><version>2.0</version><payload><login></login><logout></logout></payload></uid-response></result></response>";

    private const string InvalidCredential =
        "<response status = 'error' code = '403'><result><msg>Invalid Credential</msg></result></response>";

    private const string PerEntryFailures =
        """
        <response status="error">
          <msg><line><uid-response><version>2.0</version><payload>
            <login><entry name="CONTOSO\bad" ip="10.20.30.99" message="Invalid user name"/></login>
            <logout>
              <entry name="CONTOSO\gone" ip="10.20.30.98" message="Delete mapping failed"/>
              <entry name="CONTOSO\odd" ip="10.20.30.97" message="Something else"/>
            </logout>
          </payload></uid-response></line></msg>
        </response>
        """;

    private const string MalformedCommand =
        "<response status=\"error\"><msg><line>uid-message: payload is malformed</line></msg></response>";

    private readonly FakeTimeProvider time = new();
    private readonly ScriptedHandler handler = new();

    private FirewallClient Client(params Uri[] firewalls) =>
        new(
            new HttpClient(this.handler),
            Microsoft.Extensions.Options.Options.Create(new FirewallOptions
            {
                Endpoints = firewalls.Length > 0 ? [.. firewalls] : [FirewallA, FirewallB],
                ApiKeySecretName = "PAN_API_KEY",
                TimeoutSeconds = 10,
            }),
            name => name == "PAN_API_KEY" ? "lab-key" : null,
            this.time,
            NullLogger<FirewallClient>.Instance);

    private static Batch Batch(params MappingChange[] changes) => new(changes);

    private static MappingChange.Login Login(string user, string ip) => new(new Mapping(user, IPAddress.Parse(ip)), TimeSpan.FromMinutes(75));

    private static MappingChange.Logout Logout(string user, string ip) => new(new Mapping(user, IPAddress.Parse(ip)));

    [Fact]
    public async Task Request_is_a_form_post_with_the_key_in_a_header_never_in_the_url()
    {
        this.handler.Respond(FirewallA, HttpStatusCode.OK, Success);

        await this.Client().SubmitAsync(Batch(Login("CONTOSO\\mrossi", "10.20.30.40"), Logout("O'Brien & <Co>", "10.20.30.41")), CancellationToken.None);

        RecordedRequest request = Assert.Single(this.handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(FirewallA, request.Uri);
        Assert.DoesNotContain("key", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("lab-key", request.ApiKey);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("user-id", request.Form["type"]);
        Assert.DoesNotContain("lab-key", request.Body);

        XElement message = XElement.Parse(request.Form["cmd"]);
        Assert.Equal("uid-message", message.Name);
        Assert.Equal("update", message.Element("type")?.Value);

        XElement login = Assert.Single(message.Descendants("login").Elements("entry"));
        Assert.Equal("CONTOSO\\mrossi", login.Attribute("name")?.Value);
        Assert.Equal("10.20.30.40", login.Attribute("ip")?.Value);
        Assert.Equal("75", login.Attribute("timeout")?.Value);

        XElement logout = Assert.Single(message.Descendants("logout").Elements("entry"));
        Assert.Equal("O'Brien & <Co>", logout.Attribute("name")?.Value);
        Assert.Null(logout.Attribute("timeout"));
    }

    [Fact]
    public async Task Success_response_is_accepted_without_rejections()
    {
        this.handler.Respond(FirewallA, HttpStatusCode.OK, Success);

        SubmissionResult result = await this.Client().SubmitAsync(Batch(Login("CONTOSO\\mrossi", "10.20.30.40")), CancellationToken.None);

        Assert.Equal(FirewallA, Assert.IsType<SubmissionResult.Accepted>(result).Firewall);
        Assert.Empty(((SubmissionResult.Accepted)result).Rejections);
    }

    [Fact]
    public async Task Per_entry_failures_are_returned_and_delete_mapping_failed_is_ignored()
    {
        this.handler.Respond(FirewallA, HttpStatusCode.OK, PerEntryFailures);

        SubmissionResult result = await this.Client().SubmitAsync(Batch(Login("CONTOSO\\bad", "10.20.30.99")), CancellationToken.None);

        SubmissionResult.Accepted accepted = Assert.IsType<SubmissionResult.Accepted>(result);
        Assert.Equal(
            [
                new Rejection(RejectionKind.Login, "CONTOSO\\bad", "10.20.30.99", "Invalid user name"),
                new Rejection(RejectionKind.Logout, "CONTOSO\\odd", "10.20.30.97", "Something else"),
            ],
            accepted.Rejections);
        Assert.Single(this.handler.Requests); // no failover
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, InvalidCredential, "Invalid Credential")]
    [InlineData(HttpStatusCode.OK, MalformedCommand, "uid-message: payload is malformed")]
    [InlineData(HttpStatusCode.OK, "<html>proxy error page</html>", "unexpected response")]
    [InlineData(HttpStatusCode.OK, "not xml", "unparseable response")]
    public async Task Api_error_refuses_the_batch_without_failover(HttpStatusCode status, string body, string expected)
    {
        this.handler.Respond(FirewallA, status, body);

        SubmissionResult result = await this.Client().SubmitAsync(Batch(Login("CONTOSO\\mrossi", "10.20.30.40")), CancellationToken.None);

        SubmissionResult.ApiError error = Assert.IsType<SubmissionResult.ApiError>(result);
        Assert.Contains(expected, error.Message);
        Assert.Equal([FirewallA], this.handler.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task Connection_failure_fails_over_and_the_next_batch_starts_at_the_new_firewall()
    {
        this.handler.Fail(FirewallA, new HttpRequestException("No connection could be made"));
        this.handler.Respond(FirewallB, HttpStatusCode.OK, Success);
        FirewallClient client = this.Client();

        SubmissionResult first = await client.SubmitAsync(Batch(Login("CONTOSO\\a", "10.20.30.40")), CancellationToken.None);
        SubmissionResult second = await client.SubmitAsync(Batch(Login("CONTOSO\\b", "10.20.30.41")), CancellationToken.None);

        Assert.Equal(FirewallB, Assert.IsType<SubmissionResult.Accepted>(first).Firewall);
        Assert.Equal(FirewallB, Assert.IsType<SubmissionResult.Accepted>(second).Firewall);
        Assert.Equal([FirewallA, FirewallB, FirewallB], this.handler.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Server_error_fails_over(HttpStatusCode status)
    {
        this.handler.Respond(FirewallA, status, "");
        this.handler.Respond(FirewallB, HttpStatusCode.OK, Success);

        SubmissionResult result = await this.Client().SubmitAsync(Batch(Login("CONTOSO\\a", "10.20.30.40")), CancellationToken.None);

        Assert.Equal(FirewallB, Assert.IsType<SubmissionResult.Accepted>(result).Firewall);
    }

    [Fact]
    public async Task Silent_firewall_times_out_after_the_attempt_timeout_and_fails_over()
    {
        this.handler.Hang(FirewallA);
        this.handler.Respond(FirewallB, HttpStatusCode.OK, Success);

        Task<SubmissionResult> submit = this.Client().SubmitAsync(Batch(Login("CONTOSO\\a", "10.20.30.40")), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(submit.IsCompleted);

        this.time.Advance(TimeSpan.FromSeconds(10));
        SubmissionResult result = await submit.WaitAsync(Settle);

        Assert.Equal(FirewallB, Assert.IsType<SubmissionResult.Accepted>(result).Firewall);
    }

    [Fact]
    public async Task No_reachable_firewall_reports_every_attempt()
    {
        this.handler.Fail(FirewallA, new HttpRequestException("refused"));
        this.handler.Respond(FirewallB, HttpStatusCode.BadGateway, "");

        SubmissionResult result = await this.Client().SubmitAsync(Batch(Login("CONTOSO\\a", "10.20.30.40")), CancellationToken.None);

        SubmissionResult.Unreachable unreachable = Assert.IsType<SubmissionResult.Unreachable>(result);
        Assert.Equal([FirewallA, FirewallB], unreachable.Attempts.Select(a => a.Firewall));
        Assert.Contains("refused", unreachable.Attempts[0].Reason);
        Assert.Contains("502", unreachable.Attempts[1].Reason);
    }

    [Fact]
    public async Task Shutdown_cancels_instead_of_failing_over()
    {
        this.handler.Hang(FirewallA);
        this.handler.Respond(FirewallB, HttpStatusCode.OK, Success);
        using CancellationTokenSource stop = new();

        Task<SubmissionResult> submit = this.Client().SubmitAsync(Batch(Login("CONTOSO\\a", "10.20.30.40")), stop.Token);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit.WaitAsync(Settle));
        Assert.Equal([FirewallA], this.handler.Requests.Select(r => r.Uri));
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ApiKey, string? ContentType, string Body)
    {
        public Dictionary<string, string> Form =>
            this.Body.Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<CancellationToken, Task<HttpResponseMessage>>> script = [];

        public List<RecordedRequest> Requests { get; } = [];

        public void Respond(Uri firewall, HttpStatusCode status, string body) =>
            this.script[firewall] = _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });

        public void Fail(Uri firewall, Exception exception) =>
            this.script[firewall] = _ => Task.FromException<HttpResponseMessage>(exception);

        public void Hang(Uri firewall) =>
            this.script[firewall] = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            this.Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("X-PAN-KEY", out IEnumerable<string>? keys) ? keys.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                body));

            return await this.script[request.RequestUri!](cancellationToken);
        }
    }
}
