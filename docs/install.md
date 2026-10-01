# Installing the Proxy

The MSI (`PanRaProxy.msi`, per-machine, x64) installs the program and registers it with Windows. It never carries secrets or site settings. Those are provided after installation, so the same MSI works for every site. Terms are defined in [CONTEXT.md](../CONTEXT.md).

## What the MSI does

| Item | Detail |
|---|---|
| Program | `C:\Program Files\PanRaProxy\PanRaProxy.exe`: one self-contained executable, no .NET prerequisite |
| Service | `PanRaProxy`, automatic start, runs as the virtual account `NT SERVICE\PanRaProxy`; on failure restarts after 60 s (NFR-08). **Not started by the MSI** unless `START_SERVICE=1` |
| Event Log | Its own `PanRaProxy` log under "Applications and Services Logs"; one stable event ID per message (`Diagnostics/Log.cs`) |
| Windows Firewall | Inbound UDP `RADIUS_PORT` for `PanRaProxy.exe` only, optionally limited to `RADIUS_CLIENTS` |
| Defaults | `appsettings.json` in the install folder: replaced on every upgrade, don't edit |
| Example | `appsettings.example.json`: a starting point for the site settings |
| Folders | `%ProgramData%\PanRaProxy` and its `logs` subfolder, writable by the service account and kept on uninstall |

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

2. **Write the site settings** to `%ProgramData%\PanRaProxy\appsettings.json`. Start from `appsettings.example.json`: the RADIUS Clients' hosts, the Firewall endpoints, the domain rules, and `CaFile` if the Firewall certificate comes from a private CA. This file is yours: the MSI never creates, changes or removes it. It contains no secrets and names none.

3. **Set the two secrets** with the Proxy itself ([ADR 0007](adr/0007-two-fixed-secrets-set-by-the-binary.md)):

   ```powershell
   & "${env:ProgramFiles}\PanRaProxy\PanRaProxy.exe" --set-secret radius            # typed twice, no echo
   & "${env:ProgramFiles}\PanRaProxy\PanRaProxy.exe" --set-secret firewall-api-key
   ```

   - **`radius`** is the one secret every RADIUS Client shares.
   - **`firewall-api-key`** is the key of the Firewall's API account.
   - **From a vault or a script,** pipe the value instead of typing it: `$value | PanRaProxy.exe --set-secret radius`.
   - **Where the secrets go.** Each is encrypted with DPAPI for this machine and stored in `%ProgramData%\PanRaProxy\secrets`. The command sets the folder's ACL: SYSTEM and Administrators have full control, `NT SERVICE\PanRaProxy` can read, and nothing is inherited from ProgramData.
   - **What never happens.** A secret is never written in plaintext, never passed on a command line, and never goes through MSI properties or logs.
   - **A copied file is useless elsewhere.** It can't be decrypted on another machine: after reinstalling the machine, set the secrets again.

4. **Check, then start.** `PanRaProxy.exe --check-config` should say the configuration is valid. Then start the service with `Start-Service PanRaProxy`. Check that event 4004 ("Listening for RADIUS accounting…") is in the `PanRaProxy` log, and that a file appeared in `%ProgramData%\PanRaProxy\logs`.

**Changing a secret later:** run `--set-secret` again. If the service is running, the command restarts it so it reads the new value.

**Coming from an ADR 0003 install:** the site settings' `SecretName` and `ApiKeySecretName` keys are now reported as obsolete (event 3106). Remove them and set the two secrets as above. The old files named after them are no longer read and can be deleted.

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

## Logs

Three destinations, each filtered on its own through the `Logging` section:

| Destination | Default level | For |
|---|---|---|
| `PanRaProxy` Event Log | Warning | Monitoring: failures, rejected Logins/Logouts, unreachable Firewalls |
| `%ProgramData%\PanRaProxy\logs\panraproxy-*.log` | Information | Day-to-day detail: one file a day (`panraproxy-20260925.log`), a new one at 16 MB (`…_001.log`), the newest 14 kept |
| Console | Information, or Debug with `--debug` | Console runs |

The Event Log is the Proxy's own, not the shared Application log, so its entries can be filtered,
sized and forwarded on their own. Each message has its own event ID, listed in `src/PanRaProxy/Diagnostics/Log.cs`;
most keep the number upstream used for the same event (NFR-07).

Site settings can change any of it, e.g. quieter files and a bigger cap:

```json
"Logging": {
  "File": { "MaxFileSizeMb": 64, "RetainedFiles": 30, "LogLevel": { "Default": "Warning" } },
  "EventLog": { "LogLevel": { "Default": "Warning" } }
}
```

`"File": { "Enabled": false }` turns file logging off. If the folder can't be created, the Proxy
says so on the console and keeps running with the other two destinations. If a file can't be written
later (disk full, permissions), those lines are lost and the Proxy keeps running; it tries the file
again within 30 minutes (ADR 0004).

### Upgrading from the upstream (net462) Proxy

Event Log source names are case-insensitive, so upstream's `PanRAProxy` source is the same name as
ours. While it points at the **Application** log, the Proxy doesn't use the Event Log and says so at
startup (event 2002). An earlier console run of a pre-release build can leave the same key behind.
Remove it in an elevated PowerShell, repair the MSI, then restart the service:

```powershell
[System.Diagnostics.EventLog]::DeleteEventSource('PanRaProxy')
```

## Running from a console

The same executable runs outside the service: for diagnostics, for a live check against a lab
firewall, or on a machine without the MSI. **A console run leaves nothing behind.** It writes to the
`PanRaProxy` Event Log and to `%ProgramData%\PanRaProxy\logs` only if the MSI created them, and it never
creates registry keys or folders. It says at startup what it isn't using (events 2002 and 4006).
- **An elevated prompt** is needed only to read the installed secrets (event 2003 otherwise).
- **Without a secret file**, the secrets come from `PANRAPROXY_RADIUS_SECRET` and `PANRAPROXY_FIREWALL_API_KEY`, from any prompt.
- **A secret file wins** over the variable.

The only command that writes to the machine is `--set-secret`, and only when the administrator runs it.

```powershell
$env:PANRAPROXY_RADIUS_SECRET = "..."
$env:PANRAPROXY_FIREWALL_API_KEY = "..."
.\PanRaProxy.exe --debug `
    --Radius:Port=18131 `
    --Radius:Clients:0:Host=10.0.0.10 `
    --Firewalls:Endpoints:0=https://fw-a.example/api/
```

`--debug` logs the Proxy's own categories at Debug, so every dropped packet states its reason.
`--check-config` builds everything the service would (settings, secrets, the RADIUS Clients' names,
the CA file), reports every problem as event 3106 on the console and exits: 0 when valid, 1 when not.
It doesn't listen, and it writes no log file and no Event Log entry. Use it after editing the site
settings, before restarting the service. `--help` prints the usage and `--version` the build. An
unknown option exits with 2 instead of starting, so a typo can't start the Proxy. Any setting can be overridden as
`--Section:Key=value`, which is also how the end-to-end replay drives the Proxy
([testing.md](testing.md)).

### A dry run on production accounting

`--dry-run` takes the real path from the NPS forwarding to the Batches, and stops before the Firewall: each Login and Logout is logged (events 4007, 4008) instead of being sent. Event 2005 at startup says so.

It needs only the RADIUS side, the `radius` secret and `Radius:Clients`: no `Firewalls` section, no API key, no `CaFile`. It's meant for a trial on a VM that receives the production accounting from NPS without touching any Firewall's User-ID table.

On a VM without the MSI, leaving nothing on the machine:

```powershell
mkdir D:\PanRaProxy-test\logs                      # the only thing you create
# D:\PanRaProxy-test holds PanRaProxy.exe and an appsettings.json with
#   Radius:Clients, the UserId domain rules, and "Logging": { "File": { "Directory": "D:\\PanRaProxy-test\\logs" } }
$env:PANRAPROXY_RADIUS_SECRET = "..."              # no --set-secret: no secrets folder, no ACL
D:\PanRaProxy-test\PanRaProxy.exe --check-config --dry-run
D:\PanRaProxy-test\PanRaProxy.exe --dry-run
```

**Reading the result.** The file log gets one line per entry, e.g. `INF 4007 PanRaProxy.Firewall.DryRunSubmitter | Dry run: Login XDOMAIN\mrossi on 10.20.30.40, timeout 15 min`. You can compare it with the Mappings the Vector pipeline produces from the NPS logs.

**Using `Lookup` rules.** The VM must be domain-joined, since the lookups go to the real directory.

**Cleaning up.** Delete the folder: nothing else was written. As a service for a longer trial, put the switch in the service's command line rather than in the settings, so it can't be left on by mistake: `sc.exe config PanRaProxy binPath= "\"C:\Program Files\PanRaProxy\PanRaProxy.exe\" --dry-run"`.

## Exit codes

| Tool | Code | Meaning |
|---|---|---|
| `msiexec` | 0 / 3010 | Success / success, reboot required (not expected) |
| `msiexec` | 1603 | Failure: see the `/l*v` log. With `START_SERVICE=1`, check event 3106 first |
| `PanRaProxy.exe` | 0 | Stopped normally; `--check-config`: the configuration is valid; `--set-secret`: the secret is stored |
| `PanRaProxy.exe` | 1 | Invalid configuration (event 3106 lists every problem), listener failure (event 3103), or `--set-secret` couldn't store the secret (not elevated, no value, the two values typed differ) |
| `PanRaProxy.exe` | 2 | Unknown option on the command line, or `--set-secret` without `radius` or `firewall-api-key` |

## When the service doesn't start

Event **3106** lists every configuration problem at once: missing RADIUS Clients or Firewalls, an invalid regex, a secret that isn't set or can't be decrypted on this machine (with the exact `--set-secret` command), or a setting that is no longer used. The service exits with code 1, and Windows retries every 60 s until the configuration is fixed.

When the settings themselves are valid, 3106 also reports what only shows up while the Proxy is being built: a RADIUS Client host name that doesn't resolve, or a `Firewalls:CaFile` the service account can't read or that holds no certificate. These come one at a time: fix the first, and the next start reports the following one.

## Building the MSI

```powershell
.\build\build-msi.ps1 -Version 1.0.1
```

This runs the tests, publishes the self-contained executable to `artifacts\publish`, and builds `artifacts\msi\PanRaProxy.msi` with WiX v5. MSI validation (ICEs) needs VBScript. On hosts without it, the script skips validation and prints a warning, so build release MSIs on a host that has VBScript.

## Coexistence with the upstream (net462) Proxy

Their service names differ only in case (`panraproxy` / `PanRaProxy`), their Event Log source names are the same name case-insensitively, and both default to UDP 18131. Don't install both on the same server: uninstall the upstream one first, then remove its Event Log source as described above.
