# Specs

What the Proxy has to do, how it is built and why, and what is still open. Terms are defined in [GLOSSARY.md](../GLOSSARY.md). The environment it runs in is in [environment.md](environment.md), the decisions that are settled are in [adr/](adr/), and the behaviours that are doubtful or unverified are in [issues/](issues/README.md).

This file replaces `refactoring-spec.md` and `architecture-review.md`. Both stay in the history, as they were at `4a6e7d6` (`git show 4a6e7d6:docs/refactoring-spec.md`), together with the review of the upstream code they carried.

## 1. Origin

The Proxy turns RADIUS accounting into Palo Alto User-ID Mappings. It started as a fork of [`lithnet/pan-ra-proxy`](https://github.com/lithnet/pan-ra-proxy) (MIT) at `049992e` (2024-09-04), and is now a .NET 8 service of its own: the upstream code was removed ([ADR 0008](adr/0008-the-repository-holds-only-the-dotnet8-proxy.md)) and survives only in the history at that commit. The language choice is in [ADR 0002](adr/0002-dotnet8-refactor-over-go.md).

## 2. Functional requirements

| ID | Requirement |
|---|---|
| FR-00 | Every packet that passes the authenticator check gets an Accounting-Response, whatever happens to it afterwards. |
| FR-01 | UDP listener on a configurable port (default 18131, which differs from 1813 in case the service runs on the NPS server). |
| FR-02 | Shared secret per RADIUS Client, identified by source IP. With NPS forwarding, the RADIUS Client is the NPS server and the secret is the one in its remote RADIUS server group. |
| FR-03 | Login on `Acct-Status-Type` 1 (Start) and 3 (Interim-Update). Logout on 2 (Stop) only when `LogoutOnStop = true` (default `false`). |
| FR-04 | Drop Placeholder IPs (`0.0.0.0`, `255.255.255.255`, `255.255.255.254`; the last two are RFC 2865 §5.8 "user may select" and "NAS should select"). IPv4 only: `Framed-IPv6-Address` is ignored. |
| FR-05 | Timeout on each Login, from configuration (minutes). Default 15. The NAS interim interval is declared as `InterimIntervalMinutes` (default 5) and startup fails if the Timeout is below twice it. |
| FR-06 | The Canonical Username (NT4) comes from `UserId:Domain:Rules`, an ordered list where the first matching rule decides. A rule maps the match to a domain (`Nt4Domain`), builds any name from the match's groups (`Replace`), or asks the directory (`Lookup`, Windows only, cached for `LookupCacheMinutes`, with the other two as fallback). A username no rule matches is passed through. `UsernameRewrites` run first as an escape hatch. |
| FR-07 | Username Filter (regex), applied to the raw `User-Name` before any Username Rewrite. The default drops Machine Accounts (`\$$` and `^host/`). |
| FR-08 | Batching with a configurable maximum size and hard-timer window (defaults 200 entries / 50 ms, [ADR 0001](adr/0001-batch-with-hard-timer.md)). Within a Batch the last Login or Logout per Mapping wins. |
| FR-09 | XML API response validation that logs each failed Login or Logout. |
| FR-10 | Ordered failover between Firewalls, only on transport failures (connection, TLS, timeout) and HTTP 5xx. An API-level error (e.g. `403`, `status="error"`) does not fail over: per-Login/Logout failures are returned and logged, and a global error drops the Batch (see *No persistence* in §5). |
| FR-11 | Shared Accounts: one username may hold several Mappings at once, one per IP; a change affects only the Mapping for the IP in the packet. |

## 3. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Secrets (RADIUS shared secrets, API key) never in configuration files, MSI properties or command lines: protected files in `%ProgramData%\PanRaProxy\secrets`, set after install ([ADR 0003](adr/0003-secrets-as-protected-files-provided-after-install.md), [ADR 0007](adr/0007-two-fixed-secrets-set-by-the-binary.md)). |
| NFR-02 | TLS certificate validation on by default, using a configurable CA. `DisableCertificateValidation` exists but defaults to `false`, and when it's set the Proxy logs a warning at startup. |
| NFR-03 | Bounded queue, default capacity 10,000 (configurable), dropping the oldest item when full, with a counter of dropped items. |
| NFR-04 | Minimum metrics: packets received and dropped, Logins and Logouts sent, failed entries, queue depth. They are listed in `Diagnostics/ProxyMetrics.cs`. |
| NFR-05 | Dedicated API account with an Admin Role limited to XML API > User-ID Agent. |
| NFR-06 | No directory lookup is configured while the naming convention holds: in the capture of 2026-09-24 all three username forms use `name.surname`. A `Lookup` rule is added only for accounts where it doesn't. |
| NFR-07 | Every message carries a stable event ID through `ILogger` `EventId` (`Diagnostics/Log.cs`), written to the Proxy's own `PanRaProxy` log. No monitoring depends on the numbering ([ADR 0005](adr/0005-log-messages-stay-in-one-file.md)). |
| NFR-08 | A fatal listener failure (socket bind or close) stops the process. Windows Service recovery is set to restart it. |
| NFR-09 | The same executable runs as a service or from a console (`--debug`, `--help`, `--version`, `--check-config`, `--dry-run`), with no installation needed for a trial run, and **a console run leaves nothing behind on the machine**. |

## 4. Configuration

Defaults ship in the install folder's `appsettings.json`. Site settings go in `%ProgramData%\PanRaProxy\appsettings.json`, which overrides them (environment variables and command-line arguments override both). A full site file looks like this:

```json
{
  "Radius": {
    "Port": 18131,
    "Clients": [
      { "Host": "<nps-ip>" }
    ]
  },
  "UserId": {
    "TimeoutMinutes": 15,
    "InterimIntervalMinutes": 5,
    "LogoutOnStop": false,
    "Domain": {
      "Rules": [
        { "Match": "^(?<user>[^@\\\\]+)@(xdomain\\.local|example\\.com)$", "Nt4Domain": "XDOMAIN" },
        { "Match": "^(?<user>[^@\\\\]+)$", "Nt4Domain": "XDOMAIN" },
        { "Match": "^(?<user>[^@\\\\]+)@(?<suffix>partner\\.example)$", "Lookup": "${user}@${suffix}", "Replace": "${user}@${suffix}" }
      ],
      "LookupCacheMinutes": 480
    },
    "UsernameRewrites": [
      { "Match": "^guest-(.+)$", "Replace": "$1@example.com" }
    ],
    "UsernameFilter": "(\\$$|^host/)",
    "BatchSize": 200,
    "BatchWindowMs": 50,
    "QueueCapacity": 10000
  },
  "Firewalls": {
    "Endpoints": [ "https://fw-a.contoso.local/api/", "https://fw-b.contoso.local/api/" ],
    "CaFile": "C:\\ProgramData\\PanRaProxy\\pan-ca.pem",
    "DisableCertificateValidation": false,
    "TimeoutSeconds": 10
  }
}
```

`BatchWindowMs` is the hard timer: it starts at the first packet of a Batch and doesn't restart on later packets.

The site file names no secret. The two secrets, the RADIUS secret shared by every RADIUS Client and the Firewall API key, are set with `PanRaProxy --set-secret` ([ADR 0007](adr/0007-two-fixed-secrets-set-by-the-binary.md)).

## 5. Design

The code is `src/PanRaProxy/` (worker service, root namespace `PanRaProxy`) and its tests are `tests/PanRaProxy.Tests/` (xUnit, `FakeTimeProvider`, a stub `HttpMessageHandler`, synthetic RADIUS fixtures generated independently from RFC 2866 into `tests/fixtures/`). How to run them is in [testing.md](testing.md).

### Technology

- **Service host:** `BackgroundService` with `UseWindowsService()`, configuration through `IConfiguration` and typed options validated at startup (`ValidateOnStart`).
- **Firewall submission:** one long-lived `HttpClient` on a `SocketsHttpHandler` (`PooledConnectionLifetime` so DNS changes are picked up; the module keeps failover state, so it is not a transient typed client). Explicit per-attempt timeout, TLS trust scoped to this client, the API key in the `X-PAN-KEY` header, and a form-urlencoded `type=user-id&cmd=<uid-message>` body (PAN-OS 10 or later).
- **Queues:** bounded `System.Threading.Channels` (NFR-03).
- **Metrics:** `System.Diagnostics.Metrics`, which needs no registration at install time (NFR-04).
- **Logs:** the `PanRaProxy` Event Log, rolling files and the console, all through `ILogger`. Serilog writes the files ([ADR 0004](adr/0004-file-log-through-serilog-behind-ilogger.md)).
- **Directory lookup:** `TranslateNameW` through P/Invoke, reached through a domain rule's `Lookup`, with a cache in front of it.
- **Installer:** a WiX v5 MSI with a self-contained executable, the virtual account `NT SERVICE\PanRaProxy`, restart on failure, the Event Log source and the firewall rule. Secrets and site settings are provided after install ([INSTALL.md](../INSTALL.md)).
- **RADIUS parsing:** a small attribute reader that never throws on packet content. The upstream parser threw on unexpected lengths, which ended the listener silently ([ADR 0002](adr/0002-dotnet8-refactor-over-go.md)).

### Design notes

What was decided in the architecture reviews of 2026-09-25 and after, and why. Each is done unless it says otherwise.

- **One composition root.** `ProxyRegistration.AddPanRaProxy(configuration)` builds the whole graph and declares the shared adapters (`SecretLookup`, `TimeProvider`, `ProcessExit`, `HostResolver`) once. The five per-area registrations are `internal`. The end-to-end test composes the shipped graph and replaces only what varies, always after `AddPanRaProxy`, through `services.Replace`. Before, the test and `Program.cs` each wrote the five registrations, and the test graph had already drifted from the shipped one.
- **Canonical Username as its own module.** `Mappings/CanonicalUsername.cs` holds `CanonicalUsernameResolver`: a raw `User-Name` in, a Canonical Username out, plain `string → string`. `MappingDecider` takes it as one dependency and keeps only what an Accounting-Request means (status type, Placeholder IPs, Logout-on-Stop, Timeout). The rules keep changing, because bare names, UPNs of several suffixes and NT4 names all reach the Proxy. A result that says which rule matched, to count the Mappings not in NT4 form, is deferred: the replay leaves 13 of 40 Mappings in UPN form, so if production shows the same, that number is worth watching.
- **A domain rule validates itself.** `CanonicalUsernameRules.TryCreate` is the only definition of a valid rule, and a `DomainRule` applies itself. The Mappings module registers `CanonicalUsernameRulesValidator`, so rule failures still reach event 3106 with the other UserId settings. Every regex in the settings comes from `OptionsChecks.CreateRegex`, so a pattern is validated with the options it runs with. The regexes are compiled twice at startup, once to validate and once for the singleton; accepted, because the configuration is never reloaded without a restart.
- **Startup as a module.** `Startup/CommandLine.cs` parses the switches, and an unknown one exits 2 instead of reaching the configuration, where a typo such as `--chek-config` would have started the Proxy. `Startup/ProxyStartup.Run` goes from arguments to an exit code, and `Program.cs` is one call. The exit codes in [INSTALL.md](../INSTALL.md) are assertions in `ProxyStartupTests`.
- **A console run leaves nothing behind (NFR-09).** The Proxy never creates the Event Log or its source: `DiagnosticsRegistration.EventLogProblem` only reads the registry, and the provider is added only when the source exists and writes to the Proxy's own log. This closes a defect of the first startup code, where an elevated console run could register the source under the Application log. Log files follow the same rule: a console run writes them only if the logs folder exists. The MSI creates both.
- **The Mapping decision runs off the listener's path.** `IAccountingRequestSink` must not block, and a `Lookup` on a cache miss does (`TranslateNameW` is synchronous). The listener only queues the accepted request in `AccountingRequestQueue`; `MappingDecisionWorker` runs the whole decision, lookup included, and feeds the Batch, which is unchanged. The request queue follows NFR-03 and shows up as `panraproxy.requests.queue.depth` and `panraproxy.requests.queue.dropped`. A slow domain controller delays Logins, never reception or acknowledgements. Two other placements were weighed and set aside: a lookup inline with a time limit (it still pauses reception on every miss) and resolving inside the Batch (it stretches the ADR 0001 timer and moves deduplication into the batcher). The remaining cost is [issue 7](issues/07-a-hanging-directory-lookup-stalls-the-mapping-decision.md).
- **Configuration problems reach event 3106.** `StartupValidation` builds the hosted services once the options are valid and reports a construction failure in 3106: a RADIUS Client that doesn't resolve, one address with two secrets, a `CaFile` that exists but can't be loaded, any binding or validation exception. Construction stops at the first problem, so these come one at a time.
- **Files are written by Serilog.** The own rolling writer was 265 lines and held a defect: a failed roll ended the process ([ADR 0004](adr/0004-file-log-through-serilog-behind-ilogger.md)).
- **`Replace` means two things.** A Username Rewrite applies `Regex.Replace`, which substitutes the matched part. A domain rule's `Replace` applies `Match.Result`, where the template is the whole result. An empty `Nt4Domain`, `Replace` or `Lookup` on a rule is rejected at startup (3106). The two meanings stay, documented on the options: renaming one would break existing site settings for a small gain.
- **Named delegates, not `Func<…>` keys.** `HostResolver`, like `SecretLookup` and `ProcessExit`, so another `Func` of the same shape cannot collide in the container.
- **No persistence.** A failed Batch is lost and leaves only an Event Log entry. This is accepted because the Interim-Updates rebuild the Mappings within one interval, and it is why the Proxy keeps no Mapping state ([ADR 0006](adr/0006-no-mapping-state-in-the-proxy.md)). Monitor the Event Log.

### Rejected and closed

- **Break the Diagnostics hub.** Rejected: every `[LoggerMessage]` stays in `Diagnostics/Log.cs` and every instrument in `Diagnostics/ProxyMetrics.cs` ([ADR 0005](adr/0005-log-messages-stay-in-one-file.md)). The table of event IDs it would protect never existed, and no monitoring depends on upstream's numbering.
- **Split failover from submission.** Closed on 2026-10-05. `FirewallClient` is already one deep method. The passive HA peer accepts a User-ID call and syncs it to the active peer over HA1 ([environment.md](environment.md#answers-from-the-firewall-team-2026-10-05)), so any peer that answers is a valid target and the current rule is enough: fail over only when a peer can't be reached, and start next time from the last peer that answered. Reopen if a Firewall is ever configured that refuses or drops User-ID calls on the passive peer.

## 6. Open questions

### Install from the binary

**Status:** candidate, not planned. The repository is at MVP stage and the standalone binary is the preferred way to work with it. The MSI stays, and it has never been installed on a machine.

**Where:** `src/PanRaProxy.Setup/Package.wxs` · `Startup/ProxyStartup.cs` · `Startup/SecretCommand.cs` · `Diagnostics/DiagnosticsRegistration.EventLogProblem`.

**Problem.** The Proxy relies on the MSI for four things: the `PanRaProxy` Event Log and its source; the logs folder; the secrets folder with its protected ACL (done: `--set-secret` creates it, [ADR 0007](adr/0007-two-fixed-secrets-set-by-the-binary.md)); and the service registration with its recovery settings (NFR-08). Because a console run creates nothing (NFR-09), a Proxy installed from the binary alone would run with console and files only: no Event Log, and secrets only from environment variables.

**Solution, if needed.** Explicit `--install` and `--uninstall` commands in the startup module, run from an elevated prompt, that create and remove the remaining three things. They would act only when asked, never as a side effect of a run. They could take over from the MSI, or sit beside it for machines where an MSI is unwelcome.

**What would bring it forward.** Deploying without the MSI, or the MSI getting in the way of how the Proxy is rolled out. The cost to weigh then: two ways to install that must create the same things, unless one replaces the other.

## 7. Acceptance criteria

1. A Start or Interim-Update with a real IP produces a Mapping with the configured Timeout.
2. No Mapping is ever created for a Placeholder IP or a Machine Account.
3. A packet with a wrong authenticator is dropped with no response.
4. A malformed packet doesn't stop the listener.
5. An API error for a single Login or Logout appears in the Event Log with username and IP.
6. With the Firewall unreachable, the queue stays within its configured capacity and the service recovers on its own.
7. With `LogoutOnStop = false`, a Roam between APs never removes the Mapping.
8. Stopping the Proxy has no effect on WiFi authentication.
9. A Login reaches the Firewall within `BatchWindowMs` plus one API round trip after its accounting packet arrives.
