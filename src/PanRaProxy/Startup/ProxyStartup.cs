using System.Reflection;
using Microsoft.Extensions.Hosting.WindowsServices;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;

namespace PanRaProxy.Startup;

/// <summary>
/// What the machine offers the Proxy at startup. <see cref="Machine"/> is the real one; tests pass their own.
/// </summary>
internal sealed record StartupEnvironment(
    TextWriter Output,
    TextWriter Error,
    string SiteSettingsFile,
    bool IsWindowsService,
    Func<string?> EventLogProblem,
    Action<IServiceCollection>? ReplaceServices = null)
{
    public static StartupEnvironment Machine() => new(
        Console.Out,
        Console.Error,
        ProxyPaths.SiteSettingsFile,
        WindowsServiceHelpers.IsWindowsService(),
        OperatingSystem.IsWindows() ? DiagnosticsRegistration.EventLogProblem : () => "there is no Event Log off Windows.");
}

/// <summary>
/// From the command line to an exit code (docs/install.md): 0 success, 1 invalid configuration or a
/// listener failure, 2 an unknown option. The same executable runs as the Windows service and in a
/// console. A console run uses what the installation created (the Event Log, the logs folder) and
/// creates nothing: no registry keys, no folders.
/// </summary>
public static class ProxyStartup
{
    public static int Run(string[] args) => Run(args, StartupEnvironment.Machine());

    internal static int Run(IReadOnlyList<string> args, StartupEnvironment environment)
    {
        CommandLine command = CommandLine.Parse(args);

        if (command.Error is { } error)
        {
            environment.Error.WriteLine($"{error} Run PanRaProxy --help for the usage.");
            return 2;
        }

        switch (command.Mode)
        {
            case StartupMode.Help:
                environment.Output.WriteLine(Usage);
                return 0;

            case StartupMode.Version:
                environment.Output.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
                return 0;
        }

        bool valid;
        using (IHost host = BuildHost(command, environment))
        {
            // Every configuration problem at once, in the log, before any module starts (event 3106).
            valid = StartupValidation.Validate(host.Services);

            if (valid && command.Mode == StartupMode.Run)
            {
                host.Run();
                return 0;
            }
        }

        // After the host is disposed, so the console log has printed event 3106 first.
        if (command.Mode == StartupMode.CheckConfig)
        {
            environment.Output.WriteLine(valid
                ? "The configuration is valid."
                : "The configuration is invalid: every problem is listed above (event 3106).");
        }

        return valid ? 0 : 1;
    }

    /// <summary>
    /// The Proxy's host, with its log destinations chosen explicitly: the console always; the Event Log
    /// and the files only for a run, and in a console run only if the installation created them.
    /// </summary>
    internal static IHost BuildHost(CommandLine command, StartupEnvironment environment)
    {
        // Defaults are read from the executable's folder even when started from elsewhere.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [.. command.SettingsArgs],
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Defaults ship in the install folder; the site's settings live in %ProgramData%\PanRaProxy (docs/install.md).
        builder.Configuration.AddSiteSettings(environment.SiteSettingsFile);

        List<Action<ILogger>> notes = [];
        bool running = command.Mode == StartupMode.Run;

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddEventSourceLogger(); // dotnet-trace and dotnet-monitor

        if (command.Debug)
        {
            builder.Logging.AddFilter("PanRaProxy", LogLevel.Debug);
        }

        if (running)
        {
            if (environment.EventLogProblem() is { } problem)
            {
                notes.Add(logger => Log.EventLogNotUsed(logger, problem));
            }
            else if (OperatingSystem.IsWindows())
            {
                builder.Logging.AddEventLog();
            }

            string logs = builder.Configuration["Logging:File:Directory"] is { Length: > 0 } configured ? configured : ProxyPaths.LogsDirectory;
            if (environment.IsWindowsService || Directory.Exists(logs))
            {
                builder.Logging.AddProxyFileLog();
            }
            else
            {
                notes.Add(logger => Log.FileLogNotUsed(logger, logs));
            }
        }

        if (SecretStore.IsUnreadable(ProxyPaths.SecretsDirectory))
        {
            notes.Add(logger => Log.SecretsFolderUnreadable(logger, ProxyPaths.SecretsDirectory));
        }

        builder.Services.AddWindowsService(options => options.ServiceName = "PanRaProxy");
        builder.Services.AddPanRaProxy(builder.Configuration);
        environment.ReplaceServices?.Invoke(builder.Services);

        IHost host = builder.Build();

        ILogger startup = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PanRaProxy.Startup");
        notes.ForEach(note => note(startup));

        return host;
    }

    private const string Usage =
        """
        PanRaProxy: turns RADIUS accounting into Palo Alto User-ID mappings.

        Runs as the Windows service PanRaProxy, or from a console for diagnostics and live checks.

          PanRaProxy [--debug] [--Section:Key=value ...]
          PanRaProxy --check-config [--Section:Key=value ...]
          PanRaProxy --version
          PanRaProxy --help

          --debug         Log the Proxy's own categories at Debug level (dropped packets show their reason)
          --check-config  Validate the settings, the secrets, the RADIUS Clients' names and the CA file,
                          report every problem, and exit without listening

        Exit codes: 0 success, 1 invalid configuration or listener failure, 2 unknown option.

        A console run leaves nothing behind: it writes to the PanRaProxy Event Log and to
        %ProgramData%\PanRaProxy\logs only if the installation created them, and never creates
        registry keys or folders. It needs an elevated prompt only to read the installed secrets.

        Settings come from appsettings.json next to the executable, then
        %ProgramData%\PanRaProxy\appsettings.json, then environment variables, then the
        --Section:Key=value arguments. Secrets come from %ProgramData%\PanRaProxy\secrets
        or from environment variables of the same name.

        A console trial run against a lab firewall:

          $env:RADIUS_SECRET_NPS1 = "..."
          $env:PAN_API_KEY = "..."
          .\PanRaProxy.exe --debug --Radius:Port=18131 `
              --Radius:Clients:0:Host=10.0.0.10 --Radius:Clients:0:SecretName=RADIUS_SECRET_NPS1 `
              --Firewalls:Endpoints:0=https://fw-a.example/api/

        Logs: console, rolling files in %ProgramData%\PanRaProxy\logs, and the PanRaProxy
        Event Log (warnings and errors). See docs/install.md.
        """;
}
