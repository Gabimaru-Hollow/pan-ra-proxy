using PanRaProxy.Options;

namespace PanRaProxy.Startup;

public enum StartupMode
{
    /// <summary>Run the Proxy: as the Windows service, or in a console for diagnostics.</summary>
    Run,

    /// <summary>Validate the configuration, report every problem and exit, without listening.</summary>
    CheckConfig,

    Help,

    Version,

    /// <summary>Encrypt one of the Proxy's secrets for this machine and store it (ADR 0007).</summary>
    SetSecret,
}

/// <summary>
/// The switches the Proxy understands. Every other argument is a setting (<c>--Section:Key=value</c>)
/// and goes to configuration. A switch the Proxy doesn't know is an error rather than a setting, so a
/// typo such as <c>--chek-config</c> can't start the Proxy instead of checking it.
/// </summary>
public sealed record CommandLine(StartupMode Mode, bool Debug, IReadOnlyList<string> SettingsArgs, string? Error, string? SecretName = null, bool DryRun = false)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        StartupMode mode = StartupMode.Run;
        bool debug = false;
        bool dryRun = false;
        List<string> settings = [];

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--set-secret":
                    string? name = i + 1 < args.Count ? args[i + 1] : null;
                    return name is not null && SecretNames.All.Contains(name)
                        ? new CommandLine(StartupMode.SetSecret, debug, [], null, name)
                        : new CommandLine(mode, debug, settings, $"--set-secret needs the name of a secret: {string.Join(" or ", SecretNames.All)}.");

                case "--help" or "-h" or "-?" or "/?":
                    mode = StartupMode.Help;
                    break;

                case "--version" or "-v":
                    mode = mode == StartupMode.Help ? mode : StartupMode.Version;
                    break;

                case "--check-config":
                    mode = mode is StartupMode.Help or StartupMode.Version ? mode : StartupMode.CheckConfig;
                    break;

                case "--debug" or "-d":
                    debug = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                default:
                    if (IsUnknownSwitch(arg))
                    {
                        return new CommandLine(mode, debug, settings, $"Unknown option '{arg}'.");
                    }

                    settings.Add(arg);
                    break;
            }
        }

        return new CommandLine(mode, debug, settings, null, DryRun: dryRun);
    }

    /// <summary>
    /// Every setting has a section (<c>Radius:Port</c>), so a dash or slash argument without one is a switch.
    /// A value on its own (<c>--Radius:Port 1812</c>) doesn't start with a dash or a slash.
    /// </summary>
    private static bool IsUnknownSwitch(string arg)
    {
        if (!arg.StartsWith('-') && !arg.StartsWith('/'))
        {
            return false;
        }

        string name = arg.TrimStart('-', '/').Split('=', 2)[0];
        return !name.Contains(':');
    }
}
