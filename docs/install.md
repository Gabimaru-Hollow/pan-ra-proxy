# Installing the Proxy

The MSI (`PanRaProxy.msi`, per-machine, x64) installs the program and registers it with Windows. It never carries secrets or site settings. Those are provided after installation, so the same MSI works for every site. Terms are defined in [CONTEXT.md](../CONTEXT.md).

## What the MSI does

| Item | Detail |
|---|---|
| Program | `C:\Program Files\PanRaProxy\PanRaProxy.exe`: one self-contained executable, no .NET prerequisite |
| Service | `PanRaProxy`, automatic start, runs as the virtual account `NT SERVICE\PanRaProxy`; on failure restarts after 60 s (NFR-08). **Not started by the MSI** unless `START_SERVICE=1` |
| Event Log | Source `PanRAProxy` in the Application log (upstream's name and event IDs, spec §5.6) |
| Windows Firewall | Inbound UDP `RADIUS_PORT` for `PanRaProxy.exe` only, optionally limited to `RADIUS_CLIENTS` |
| Defaults | `appsettings.json` in the install folder: replaced on every upgrade, don't edit |
| Example | `appsettings.example.json`: a starting point for the site settings |
| Helper | `Set-PanRaProxySecret.ps1`: sets secrets after installation |

## Public properties

| Property | Default | Meaning |
|---|---|---|
| `RADIUS_PORT` | `18131` | UDP port opened in Windows Firewall. Must match `Radius:Port` in the site settings. |
| `RADIUS_CLIENTS` | *(empty = any)* | Remote addresses allowed by the firewall rule, comma-separated, e.g. `10.0.0.10,10.0.0.11` (the Forwarding Servers). |
| `START_SERVICE` | `0` | `1` starts the service at the end of the install. Use it on upgrades, once site settings and secrets exist. If the service fails to start, the install fails and rolls back. |

## First installation

Run these steps in order from the setup tool, elevated.

1. **Install the MSI.**

   ```bat
   msiexec /i PanRaProxy.msi /qn /l*v "%TEMP%\PanRaProxy-install.log" RADIUS_CLIENTS=10.0.0.10
   ```

2. **Write the site settings** to `%ProgramData%\PanRaProxy\appsettings.json`. Start from `appsettings.example.json`: RADIUS Clients with their `SecretName`, Firewall endpoints, Username Rewrite rules, and `CaFile` if the Firewall certificate comes from a private CA. This file is yours: the MSI never creates, changes or removes it. It contains no secrets.

3. **Set the secrets**, one per `SecretName` / `ApiKeySecretName` in the site settings, and start the service:

   ```powershell
   $ps = "${env:ProgramFiles}\PanRaProxy\Set-PanRaProxySecret.ps1"
   & $ps -Name RADIUS_SECRET_NPS1 -Value $radiusSecret        # SecureString from your vault
   & $ps -Name PAN_API_KEY        -Value $apiKey -Restart     # -Restart starts the service
   ```

   Secrets are files in `%ProgramData%\PanRaProxy\secrets`, readable only by SYSTEM, Administrators and `NT SERVICE\PanRaProxy`. They are never passed on a command line or through MSI properties, so they don't end up in MSI logs. Called without `-Value` in an interactive session, the script prompts.

4. **Check** that the service is running and that event 4004 ("Listening for RADIUS accounting…") is in the Application log.

## Upgrade

```bat
msiexec /i PanRaProxy-1.0.1.msi /qn /l*v "%TEMP%\PanRaProxy-upgrade.log" START_SERVICE=1
```

The MSI does a major upgrade: it stops the service, replaces the program and defaults, and keeps the site settings and secrets. With `START_SERVICE=1` it starts the service again. Pass `RADIUS_CLIENTS` again if you used it, because MSI properties aren't remembered between versions.

## Uninstall

```bat
msiexec /x PanRaProxy.msi /qn
```

This removes the program, the service, the Event Log source and the firewall rule. `%ProgramData%\PanRaProxy` (site settings and secrets) is kept. Delete it yourself if the Proxy won't come back.

## Exit codes

| Tool | Code | Meaning |
|---|---|---|
| `msiexec` | 0 / 3010 | Success / success, reboot required (not expected) |
| `msiexec` | 1603 | Failure: see the `/l*v` log. With `START_SERVICE=1`, check event 3106 first |
| `Set-PanRaProxySecret.ps1` | 0 / 1 | Success / failure (not elevated, service not installed, empty value, service didn't reach Running within 30 s) |
| `PanRaProxy.exe` | 1 | Invalid configuration (event 3106 lists every problem) or listener failure (event 3103) |

## When the service doesn't start

Event **3106** lists every configuration problem at once: missing RADIUS Clients or Firewalls, an invalid regex, or a secret that isn't set (with the exact `Set-PanRaProxySecret.ps1` command). The service exits with code 1, and Windows retries every 60 s until the configuration is fixed.

## Building the MSI

```powershell
.\build\build-msi.ps1 -Version 1.0.1
```

This runs the tests, publishes the self-contained executable to `artifacts\publish`, and builds `artifacts\msi\PanRaProxy.msi` with WiX v5. MSI validation (ICEs) needs VBScript. On hosts without it, the script skips validation and prints a warning, so build release MSIs on a host that has VBScript.

## Coexistence with the upstream (net462) Proxy

Both use the Event Log source `PanRAProxy` and default to UDP 18131, and their service names differ only in case (`panraproxy` / `PanRaProxy`), which Windows treats as the same. Don't install both on the same server.
