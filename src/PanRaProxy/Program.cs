using PanRaProxy.Diagnostics;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;

// Defaults are read from the executable's folder even when started from elsewhere.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Defaults ship in the install folder; the site's settings live in %ProgramData%\PanRaProxy (docs/install.md).
builder.Configuration.AddSiteSettings(ProxyPaths.SiteSettingsFile);

builder.Services.AddWindowsService(options => options.ServiceName = "PanRaProxy");
builder.Services.AddProxyOptions(builder.Configuration);
builder.Services.AddProxyDiagnostics();
builder.Services.AddMappingDecision();
builder.Services.AddRadiusAccounting();
builder.Services.AddFirewallSubmission();

using IHost host = builder.Build();

// Report every configuration problem at once, in the Event Log, before any module starts.
if (!StartupValidation.Validate(host.Services))
{
    return 1;
}

host.Run();
return 0;
