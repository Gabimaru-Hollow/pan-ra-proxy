namespace PanRaProxy.Startup;

public enum StartupMode
{
    /// <summary>Run the Proxy: as the Windows service, or in a console for diagnostics.</summary>
    Run,

    /// <summary>Validate the configuration, report every problem and exit, without listening.</summary>
    CheckConfig,

    Help,

    Version,
}

/// <summary>
/// The switches the Proxy understands. Every other argument is a setting (<c>--Section:Key=value</c>)
/// and goes to configuration. A switch the Proxy doesn't know is an error rather than a setting, so a
/// typo such as <c>--chek-config</c> can't start the Proxy instead of checking it.
/// </summary>
public sealed record CommandLine(StartupMode Mode, bool Debug, IReadOnlyList<string> SettingsArgs, string? Error)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        StartupMode mode = StartupMode.Run;
        bool debug = false;
        List<string> settings = [];

        foreach (string arg in args)
        {
            switch (arg)
            {
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

                default:
                    if (IsUnknownSwitch(arg))
                    {
                        return new CommandLine(mode, debug, settings, $"Unknown option '{arg}'.");
                    }

                    settings.Add(arg);
                    break;
            }
        }

        return new CommandLine(mode, debug, settings, null);
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
