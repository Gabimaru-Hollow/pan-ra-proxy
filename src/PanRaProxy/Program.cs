using System.Reflection;
using PanRaProxy;
using PanRaProxy.Diagnostics;
using PanRaProxy.Options;

// Switches handled here; everything else is configuration (--Section:Key=value).
bool debug = args.Any(a => a is "--debug" or "-d");
bool help = args.Any(a => a is "--help" or "-h" or "-?" or "/?");
bool version = args.Any(a => a is "--version" or "-v");
string[] settingsArgs = args.Where(a => a is not ("--debug" or "-d" or "--help" or "-h" or "-?" or "/?" or "--version" or "-v")).ToArray();

if (help)
{
    Usage();
    return 0;
}

if (version)
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
    return 0;
}

// Defaults are read from the executable's folder even when started from elsewhere.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = settingsArgs,
    ContentRootPath = AppContext.BaseDirectory,
});

// Defaults ship in the install folder; the site's settings live in %ProgramData%\PanRaProxy (docs/install.md).
builder.Configuration.AddSiteSettings(ProxyPaths.SiteSettingsFile);

builder.Logging.AddProxyFileLog();
if (debug)
{
    builder.Logging.AddFilter("PanRaProxy", LogLevel.Debug);
}

builder.Services.AddWindowsService(options => options.ServiceName = "PanRaProxy");
builder.Services.AddPanRaProxy(builder.Configuration);

if (OperatingSystem.IsWindows() && DiagnosticsRegistration.EnsureEventLog() is { } eventLogProblem)
{
    Console.Error.WriteLine($"Event Log: {eventLogProblem}");
}

using IHost host = builder.Build();

// Report every configuration problem at once, in the log, before any module starts.
if (!StartupValidation.Validate(host.Services))
{
    return 1;
}

host.Run();
return 0;

static void Usage() => Console.WriteLine(
    """
    PanRaProxy: turns RADIUS accounting into Palo Alto User-ID mappings.

    Runs as the Windows service PanRaProxy, or from a console for a trial run.
    A console run needs an elevated session only to create the Event Log the first time.

      PanRaProxy [--debug] [--Section:Key=value ...]
      PanRaProxy --version
      PanRaProxy --help

      --debug     Log the Proxy's own categories at Debug level (dropped packets show their reason)

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
    """);
