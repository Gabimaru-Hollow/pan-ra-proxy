using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;

namespace PanRaProxy.Tests.Firewall;

/// <summary>
/// A dry run logs what each Batch would have sent, one entry per line, and sends nothing.
/// </summary>
public class DryRunSubmitterTests
{
    [Fact]
    public async Task Every_login_and_logout_is_logged_and_nothing_is_sent()
    {
        FakeLogger<DryRunSubmitter> logger = new();
        Batch batch = new(
        [
            new MappingChange.Login(new Mapping(@"XDOMAIN\mrossi", IPAddress.Parse("10.20.30.40")), TimeSpan.FromMinutes(15)),
            new MappingChange.Logout(new Mapping(@"XDOMAIN\bverdi", IPAddress.Parse("10.20.30.41"))),
        ]);

        SubmissionResult result = await new DryRunSubmitter(logger).SubmitAsync(batch, CancellationToken.None);

        Assert.IsType<SubmissionResult.NotSent>(result);
        IReadOnlyList<FakeLogRecord> records = logger.Collector.GetSnapshot();
        Assert.Equal([4007, 4008], records.Select(r => r.Id.Id));
        Assert.All(records, r => Assert.Equal(LogLevel.Information, r.Level));
        Assert.Contains(@"Login XDOMAIN\mrossi on 10.20.30.40", records[0].Message);
        Assert.Contains("15 min", records[0].Message);
        Assert.Contains(@"Logout XDOMAIN\bverdi on 10.20.30.41", records[1].Message);
    }
}
