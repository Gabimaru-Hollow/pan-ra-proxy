# Refactoring spec: fork of lithnet/pan-ra-proxy

Starting spec for reusing and refactoring `lithnet/pan-ra-proxy`. Terms are defined in [CONTEXT.md](../CONTEXT.md). The deployment context is in [deployment.md](deployment.md), and the decisions are in [adr/](adr/).

## 1. Deliverables

1. Corrective patches to the current code (§3.3), enough for a first test alongside the Vector pipeline.
2. A refactor to .NET 8 (§5; see [ADR 0002](adr/0002-dotnet8-refactor-over-go.md)).

## 2. Upstream repository state

| Item | Value |
|---|---|
| Repository | `https://github.com/lithnet/pan-ra-proxy` (MIT) |
| HEAD analysed / fork baseline | `049992e` (2024-09-04, README only) |
| Last substantial code change | `7145fb5` (2024-02-29): SDK-style project, 32/64-bit fix, cache of unresolvable domains, build pipeline |
| Target framework | `net462` in the csproj; `app.config` declares runtime 4.8 |
| Size | ~2,900 lines of C#: 521 in the attribute parser, ~714 in ConfigSections |
| Installer | WiX (`Lithnet.Pan.RAProxy.Setup`) |

The project has low activity but isn't abandoned.

### 2.1 Code map

| File | Lines | Responsibility |
|---|---|---|
| `RadiusAccounting/AccountingListener.cs` | 269 | UDP listener, Request Authenticator check, Accounting-Response construction |
| `RadiusAccounting/RadiusAttribute.cs` | 521 | RADIUS attribute parsing and serialization |
| `RadiusAccounting/AccountingRequest.cs` | 43 | Request model; checks that `Acct-Status-Type` and `User-Name` are present |
| `MessageQueue.cs` | 351 | Queue, Batching, accounting → Login/Logout, Username Rewrite, Name Translation, Username Filter |
| `PanApi/Message.cs` | 276 | HTTP submission, response parsing, Firewall failover |
| `PanApi/Entry.cs`, `UidMessage.cs`, `Payload.cs`, `Login.cs`, `Logout.cs` | — | XML model of the `uid-message` |
| `ConfigSections/*` | 714 | Custom `System.Configuration` sections |
| `Logging.cs` | 166 | Event Log and 11 PerformanceCounters |
| `WinApi/NativeMethods.cs` | 43 | `TranslateName` P/Invoke (Name Translation) |

Upstream config names don't match the glossary. `<radius-servers>` lists the **RADIUS Clients**, and `<radius-client>` configures the **Proxy's** own listener. `pan-api-endpoint` is a **Firewall**. The .NET 8 configuration (§5.4) uses the glossary names.

## 3. Code review findings

### 3.1 Strengths to keep in the refactor

- **Correct RADIUS protocol handling** (`AccountingListener.cs`): MD5 check of the Request Authenticator using the secret of the source RADIUS Client, silent discard of unauthenticated packets, and an Accounting-Response with a computed Response Authenticator that echoes the required attributes (`Proxy-State`).
- **XML API response validation** (`Message.cs`): parses `/response/@status`. On error, it extracts each failed entry from `/response/msg/line/uid-response/payload/{login,logout}/entry` and logs it with a dedicated event ID. "delete mapping failed" errors are ignored except in debug. The Vector `http` sink can't do this.
- **Deduplication within a Batch** (`MessageQueue.cs:203`): a Login removes a pending Logout for the same Mapping, so Stop → Start in the same Batch produces only the Login. See P1-4.
- **Firewall failover** (`Message.cs`, `Config.Failover()`).
- **Username handling**: `username-rewrites` regex rules (Username Rewrite), optional NT4/UPN Name Translation with an 8-hour cache of unresolvable domains, and a `username-filter` regex (Username Filter).

### 3.2 Issues

| ID | Priority | Where | Problem | Fix |
|---|---|---|---|---|
| P1-1 | **High** | `MessageQueue.cs:153` | Every `Framed-IP-Address` becomes a Mapping without checking its value, so a Placeholder IP (`255.255.255.255`, `0.0.0.0`) produces a real Mapping. | Drop Placeholder IPs before creating the Login. |
| P1-2 | **High** | `AccountingListener.cs:72` | The loop catches only `SocketException`. Any other parsing exception ends the `Task`, which nothing observes: the service keeps running but stops listening. | Catch-all in the loop: log and continue. |
| P1-3 | **High** | `Entry.cs:17`, `MessageQueue.cs` | The `Timeout` property exists but is never set, so every Mapping uses the Firewall's global User-ID timeout. | Set the Timeout on Logins from configuration. |
| P1-4 | **High** | `Entry.cs:63-69` | `IEquatable<Entry>.Equals` compares `Timeout`, but `Equals(object)` doesn't. `List.Remove` uses the former. Once P1-3 gives Logins a Timeout and Logouts have none, a Login no longer cancels the pending Logout for the same Mapping. | Base equality on username + IP only (the Mapping identity). Fix together with P1-3. |
| P2-1 | Medium | `MessageQueue.cs:211` | Logout on Accounting-Stop is always on. On a Roam, a Stop from the old AP that arrives in a later Batch than the Start from the new AP removes a valid Mapping. | Make Logout-on-Stop configurable, **default off**. |
| P2-2 | Medium | `Message.cs:213-217` | The API key is sent in the query string (`?key=`), so it ends up in URLs and any intermediate logs. | `X-PAN-KEY` header. |
| P2-3 | Medium | `Message.cs:245` | `HttpWebRequest` has no explicit timeout (default 100 s). With a single-threaded queue, a Firewall that doesn't respond blocks all processing. | Explicit timeout (e.g. 10 s); `HttpClient` in the refactor. |
| P2-4 | Medium | `MessageQueue.cs:28` | `BlockingCollection` has no maximum capacity, so memory grows without limit during a long API outage. | Bounded capacity that drops the oldest items, with a counter. |
| P2-5 | Medium | `MessageQueue.cs:46-65` | The batch wait restarts after every packet, so a steady trickle can hold a Batch open for up to `BatchSize × BatchWait` (about 10 s). | Hard-timer window from the first packet ([ADR 0001](adr/0001-batch-with-hard-timer.md)). |
| P3-1 | Low | `Message.cs:199` | Failover calls `Submit()` recursively, and `retryAttempts` is per message instance. | Explicit loop over the Firewalls. |
| P3-2 | Low | `AccountingListener.cs` | The length declared in the RADIUS header isn't compared with the received buffer length. | Check `requestLength <= data.Length` before parsing. |
| P3-3 | Low | `Program.cs:49` | `disable-certificate-validation` installs a callback that accepts any certificate. | Keep it as a flag, default `false`, and keep the startup warning. Add a configurable CA so it's never needed. |
| P3-4 | Low | — | No persistence: a failed Batch is lost, leaving only an Event Log entry. | Acceptable, because Interim-Updates rebuild the Mappings. Monitor the Event Log. |

### 3.3 Minimal patches for the first test (current code)

P1-1, P1-2, and P1-3 together with P1-4 are enough for a test alongside the Vector pipeline. Each is a change of a few lines and none requires changing the target framework.

## 4. Decisions

| Decision | Where recorded |
|---|---|
| Hard-timer Batch window instead of one call per Login | [ADR 0001](adr/0001-batch-with-hard-timer.md) |
| .NET 8 refactor for now; Go rewrite set aside | [ADR 0002](adr/0002-dotnet8-refactor-over-go.md) |
| Logout-on-Stop: config flag, default `false` | §5.4 |
| Certificate validation: config flag `DisableCertificateValidation`, default `false` | §5.4 |
| Canonical Username from ordered domain rules; the directory lookup is one rule action among others, cached | §5.4, FR-06 |

## 5. Refactor target: .NET 8

### 5.1 Replacements

| Current | Target | Note |
|---|---|---|
| `ServiceBase` | `BackgroundService` + `UseWindowsService()` | |
| `ConfigurationManager` + `ConfigSections/*` | `IConfiguration` on `appsettings.json` + typed options | Removes about 714 lines |
| `HttpWebRequest` multipart | One long-lived `HttpClient` on a `SocketsHttpHandler` (`PooledConnectionLifetime` for DNS changes; the Firewall module keeps failover state, so not a transient typed client), explicit per-attempt timeout, TLS trust scoped to this client, `X-PAN-KEY` header, form-urlencoded `type=user-id&cmd=<uid-message>` (PAN-OS ≥ 10) | Fixes P2-2, P2-3 |
| `System.Runtime.Caching.MemoryCache` | `Microsoft.Extensions.Caching.Memory` | |
| Unbounded `BlockingCollection` | Bounded `System.Threading.Channels` | Fixes P2-4 |
| 11 `PerformanceCounter`s | `System.Diagnostics.Metrics` | No registration at install time |
| `EventLog.WriteEntry` | `ILogger` with the EventLog provider | |
| `TranslateName` P/Invoke | Unchanged, reached through a domain rule's `Lookup`, with a cache in front of it | |
| WiX v3 installer (net462, NETWORK SERVICE) | WiX v5 MSI: self-contained executable, virtual account `NT SERVICE\PanRaProxy`, restart on failure, Event Log source, firewall rule; secrets and site settings provided after install ([install.md](install.md), [ADR 0003](adr/0003-secrets-as-protected-files-provided-after-install.md)) | |

Carry over unchanged: `RadiusAttribute.cs`, the authentication logic in `AccountingListener.cs`, the response parsing in `Message.cs`, and the Batch deduplication in `MessageQueue.cs` (with P1-4 fixed).

### 5.0 Layout

`src/PanRaProxy/` (worker service, root namespace `PanRaProxy`) and `tests/PanRaProxy.Tests/` (xUnit, `FakeTimeProvider`, stub `HttpMessageHandler`, synthetic RADIUS fixtures generated independently from RFC 2866 into `tests/fixtures/`). The net462 project stays, with only the §3.3 patches, until the parallel test is signed off.

### 5.1b Build order

1. Options (only the slices each module needs, `ValidateOnStart`)
2. Accounting packet module + listener adapter, with the fixture generator
3. Mapping decision module
4. Batch module
5. Firewall submission module (the Proxy works end to end from here)
6. Logging and metrics, wired in as each module lands

Each step ships with its tests.

### 5.2 Functional requirements

| ID | Requirement |
|---|---|
| FR-01 | UDP listener on a configurable port (default 18131, which differs from 1813 in case the service runs on the NPS server). |
| FR-02 | Shared secret per RADIUS Client, identified by source IP. With NPS forwarding, the RADIUS Client is the NPS server and the secret is the one in its remote RADIUS server group. |
| FR-00 | Every packet that passes the authenticator check gets an Accounting-Response, whatever happens to it afterwards. |
| FR-03 | Login on `Acct-Status-Type` 1 (Start) and 3 (Interim-Update). Logout on 2 (Stop) only when `LogoutOnStop = true` (default `false`). |
| FR-04 | Drop Placeholder IPs (`0.0.0.0`, `255.255.255.255`, `255.255.255.254`; the last two are RFC 2865 §5.8 "user may select" and "NAS should select"). IPv4 only: `Framed-IPv6-Address` is ignored. |
| FR-05 | Timeout on each Login, from configuration (minutes). Default 15. The NAS interim interval is declared as `InterimIntervalMinutes` (default 5) and startup fails if the Timeout is below twice it. |
| FR-06 | The Canonical Username (NT4) comes from `UserId:Domain:Rules`, an ordered list where the first matching rule decides. A rule maps the match to a domain (`Nt4Domain`), builds any name from the match's groups (`Replace`), or asks the directory (`Lookup`, Windows only, cached for `LookupCacheMinutes`, with the other two as fallback). A username no rule matches is passed through. `UsernameRewrites` run first as an escape hatch. |
| FR-07 | Username Filter (regex), applied to the raw `User-Name` before any Username Rewrite. The default drops Machine Accounts (`\$$` and `^host/`). |
| FR-08 | Batching with a configurable maximum size and hard-timer window (upstream sizes: 200 entries / 50 ms). Within a Batch the last Login or Logout per Mapping wins. |
| FR-09 | XML API response validation that logs each failed Login or Logout. |
| FR-11 | Shared Accounts: one username may hold several Mappings at once, one per IP; a change affects only the Mapping for the IP in the packet. |
| FR-10 | Ordered failover between Firewalls, only on transport failures (connection, TLS, timeout) and HTTP 5xx. An API-level error (e.g. `403`, `status="error"`) does not fail over: per-Login/Logout failures are returned and logged, and a global error drops the Batch (P3-4). |

### 5.3 Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Secrets (RADIUS shared secrets, API key) never in configuration files, MSI properties or command lines: protected files in `%ProgramData%\PanRaProxy\secrets`, set after install ([ADR 0003](adr/0003-secrets-as-protected-files-provided-after-install.md)). |
| NFR-02 | TLS certificate validation on by default, using a configurable CA. `DisableCertificateValidation` exists but defaults to `false`, and when it's set the Proxy logs a warning at startup. |
| NFR-03 | Bounded queue, default capacity 10,000 (configurable), dropping the oldest item when full, with a counter of dropped items. |
| NFR-04 | Minimum metrics: packets received and dropped, Logins and Logouts sent, failed entries, queue depth. |
| NFR-05 | Dedicated API account with an Admin Role limited to XML API > User-ID Agent. |
| NFR-07 | Event Log entries keep the upstream event IDs (`Logging.cs`) through `ILogger` `EventId`. |
| NFR-08 | A fatal listener failure (socket bind or close) stops the process. Windows Service recovery is set to restart it. |
| NFR-06 | No directory lookup is configured while the naming convention holds: in the capture of 2026-09-24 all three username forms use `name.surname`. A `Lookup` rule is added only for accounts where it doesn't. |

### 5.4 Configuration

Defaults ship in the install folder's `appsettings.json`. Site settings go in `%ProgramData%\PanRaProxy\appsettings.json`, which overrides them (environment variables and command-line arguments override both). A full site file looks like this:

```json
{
  "Radius": {
    "Port": 18131,
    "Clients": [
      { "Host": "<nps-ip>", "SecretName": "RADIUS_SECRET_NPS1" }
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
    "ApiKeySecretName": "PAN_API_KEY",
    "CaFile": "C:\\ProgramData\\PanRaProxy\\pan-ca.pem",
    "DisableCertificateValidation": false,
    "TimeoutSeconds": 10
  }
}
```

`BatchWindowMs` is the hard timer: it starts at the first packet of a Batch and doesn't restart on later packets.

### 5.5 Tests

- **Unit and module** (`tests/PanRaProxy.Tests`): request and response authenticators against packets generated independently from RFC 2866; the attribute reader against malformed and random input; accounting -> Login/Logout (Placeholder IP, Machine Account, Logout-on-Stop, the three username forms, Shared Accounts); Batch window and queue limits on a fake clock; XML API responses (success, global error, per-entry failures); TLS trust; options validation.
- **End-to-end** ([testing.md](testing.md)): `tools/e2e/live-replay.py` replays a live capture against the real Proxy, with a mock PAN-OS endpoint over HTTPS, and compares the Mappings with those expected from the FreeRADIUS detail file of the same capture.

## 6. Acceptance criteria

1. A Start or Interim-Update with a real IP produces a Mapping with the configured Timeout.
2. No Mapping is ever created for a Placeholder IP or a Machine Account.
3. A packet with a wrong authenticator is dropped with no response.
4. A malformed packet doesn't stop the listener (P1-2).
5. An API error for a single Login or Logout appears in the Event Log with username and IP.
6. With the Firewall unreachable, the queue stays within its configured capacity and the service recovers on its own.
7. With `LogoutOnStop = false`, a Roam between APs never removes the Mapping.
8. Stopping the Proxy has no effect on WiFi authentication.
9. A Login reaches the Firewall within `BatchWindowMs` plus one API round trip after its accounting packet arrives.
