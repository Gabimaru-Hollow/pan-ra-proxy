using PanRaProxy.Diagnostics;
using PanRaProxy.Mappings;

namespace PanRaProxy.Firewall;

/// <summary>
/// Where a Batch goes: the Firewalls (<see cref="FirewallClient"/>), or nowhere in a dry run
/// (<see cref="DryRunSubmitter"/>).
/// </summary>
public interface IBatchSubmitter
{
    Task<SubmissionResult> SubmitAsync(Batch batch, CancellationToken cancellationToken);
}

/// <summary>
/// <c>--dry-run</c>: logs every Login and Logout a Batch would have sent, one event each (4007, 4008), and
/// sends nothing. For a trial on production accounting that must not touch the Firewalls' User-ID tables.
/// </summary>
public sealed class DryRunSubmitter(ILogger<DryRunSubmitter> logger) : IBatchSubmitter
{
    public Task<SubmissionResult> SubmitAsync(Batch batch, CancellationToken cancellationToken)
    {
        foreach (MappingChange change in batch.Changes)
        {
            switch (change)
            {
                case MappingChange.Login login:
                    Log.DryRunLogin(logger, login.Mapping.Username, login.Mapping.IpAddress, (int)Math.Ceiling(login.Timeout.TotalMinutes));
                    break;

                case MappingChange.Logout logout:
                    Log.DryRunLogout(logger, logout.Mapping.Username, logout.Mapping.IpAddress);
                    break;
            }
        }

        return Task.FromResult<SubmissionResult>(new SubmissionResult.NotSent());
    }
}
