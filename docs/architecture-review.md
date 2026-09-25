# Architecture review — 2026-09-25

Six **candidates, not decisions**: places where the .NET 8 code is harder to change or to test than it needs to be, with the file and line that makes each one concrete. None of them contradicts [ADR 0001](adr/0001-batch-with-hard-timer.md), [0002](adr/0002-dotnet8-refactor-over-go.md) or [0003](adr/0003-secrets-as-protected-files-provided-after-install.md), and none changes what the Proxy does: the terms are from [CONTEXT.md](../CONTEXT.md), the requirements from [refactoring-spec.md](refactoring-spec.md).

Reviewed at `ed83069` on `refactor/net8-fork`, with 150 tests green. The review looked at what changed recently (`Mappings`, `Options`, `Diagnostics`, `Program.cs`), not at the whole repository, and skipped the net462 code, which is kept only until the parallel test with Vector is signed off.

| # | Candidate | Strength |
|---|---|---|
| 1 | [One composition root](#1-one-composition-root) | Strong |
| 2 | [Canonical Username as its own module](#2-canonical-username-as-its-own-module) | Strong |
| 3 | [A domain rule that validates itself](#3-a-domain-rule-that-validates-itself) | Strong |
| 4 | [Break the Diagnostics hub](#4-break-the-diagnostics-hub) | Worth exploring |
| 5 | [Make startup a module, not a script](#5-make-startup-a-module-not-a-script) | Worth exploring |
| 6 | [Split failover from submission](#6-split-failover-from-submission) | Speculative |

Suggested order: 1 first, because it is cheap and protects the evidence the rest rests on; then 2 and 3 together, because they are the same change seen from two sides.

---

## 1. One composition root

**Where:** `src/PanRaProxy/Program.cs:43-47` · `tests/PanRaProxy.Tests/EndToEndTests.cs:54-58` · the five `Add…` methods in `Options/OptionsRegistration.cs`, `Radius/RadiusRegistration.cs`, `Mappings/MappingRegistration.cs`, `Firewall/FirewallRegistration.cs`, `Diagnostics/DiagnosticsRegistration.cs`.

**Problem.** The same five registrations are written twice, once in `Program.cs` and once in the end-to-end test, so the graph the test proves can differ from the graph that ships. It already has: `AddProxyDiagnostics` had to be added to the test by hand when the metrics arrived, or the test wouldn't resolve `ProxyMetrics`.

Four registration modules also re-declare the same shared adapters with `TryAdd`:

| Adapter | Declared in |
|---|---|
| `SecretLookup` | Options, Radius, Firewall |
| `TimeProvider` | Mappings, Firewall |
| `ProcessExit`, `Func<string, IPAddress[]>` | Radius |

`TryAdd` means the first registration wins, so the order the five calls run in is load-bearing and nothing says so.

**Solution.** One module owns the graph: `services.AddPanRaProxy(configuration)`. The per-area registrations become its implementation, and the shared adapters are declared once. The end-to-end test then composes the shipped graph and replaces only what varies: the `HttpMessageHandler`, `ProcessExit` and `SecretLookup`.

**Deletion test.** Deleting it puts the ordering and the shared adapters back into every caller: complexity concentrates, so it earns its keep.

**What improves.** Locality: one place says how the Proxy is built. Leverage: one call for every caller, including future ones (a replay tool, a config-check command). The test exercises what ships.

---

## 2. Canonical Username as its own module

**Where:** `src/PanRaProxy/Mappings/MappingDecider.cs:41-48, 93-129` (`ToCanonicalUsername`, the private `DomainRule` record, `CreateRegex`) · `Mappings/NameTranslation.cs` · `Mappings/CachingNameTranslator.cs` · `tests/PanRaProxy.Tests/Mappings/CanonicalUsernameTests.cs`.

**Problem.** The Mapping decision module holds two subjects. One is what an Accounting-Request means: status type, Placeholder IPs, Logout-on-Stop, Timeout. The other is how a received username becomes the Canonical Username: Username Rewrites, the ordered domain rules, `${user}` templates, the directory lookup and its cache. The second is about half the module, it isn't in its name, and it is the part that changes most: it changed twice in the week of 2026-09-22 (multiple UPN suffixes, then rules).

The test surface shows it. `CanonicalUsernameTests` has to build an `AccountingRequest` with a status type and an IP address to ask what a username becomes, and reads the answer back out of a `MappingChange`.

**Solution.** A module whose interface is that question: a raw `User-Name` in, a Canonical Username out. Behind it the rewrites, the rules, the templates and the lookup; the Mapping decision module takes it as one dependency.

**What improves.** Tests ask the real question. Rule changes stay in one module. The decision module shrinks to the concept it is named after. The lookup (Windows-only, cached) sits behind an internal seam instead of being visible in the decision code.

---

## 3. A domain rule that validates itself

**Where:** `src/PanRaProxy/Options/OptionsValidators.cs:116-148` (`ValidateDomainRule`) · `Options/DomainOptions.cs` · `Mappings/MappingDecider.cs:46-48, 93-129`.

**Problem.** What a domain rule means is written twice:

| Rule semantics | Validator | Decision module |
|---|---|---|
| `Nt4Domain` needs a `user` group | `OptionsValidators.cs:134` | `MappingDecider.cs:117` expands `${user}` |
| A rule needs at least one action | `OptionsValidators.cs:129` | `MappingDecider.cs:110-120` falls through actions |
| `Lookup` needs Windows | `OptionsValidators.cs:139` | the translator is a no-op elsewhere |

A fourth action would have to be added in both places, and nothing fails if only one is updated. The regexes are also compiled twice: once to validate, once to run.

**Solution.** A rule set that is valid by construction: `TryCreate(options)` returns either the compiled rules or the reasons it can't. The validator reports those reasons; the decision module (or candidate 2's module) applies the result. `ValidateDomainRule` disappears.

**What improves.** One module defines what a rule is, what it means and how it runs. Invalid rules stop being representable past startup. The startup message wording (event 3106) stays where the checks are.

---

## 4. Break the Diagnostics hub

**Where:** `src/PanRaProxy/Diagnostics/Log.cs` (93 lines, `using` of `Radius`, `Mappings`, `Firewall`) · `Diagnostics/ProxyMetrics.cs:24, 36-37`.

**Problem.** Every module depends on Diagnostics, and Diagnostics depends on every module: `Log` takes `AccountingRequest`, `DiscardReason`, `DropReason` and `Uri firewall`, and `ProxyMetrics` takes `MappingBatcher` in its constructor to observe queue depth and drops. The dependency runs both ways, and adding one log line to the Firewall module means editing a file shared with the listener.

**Solution.** Each module keeps its own log messages and counters next to the code that raises them. Diagnostics keeps what is genuinely shared: the event-ID constants, the file log, the Event Log settings and the meter. The Batch module exposes a small queue snapshot instead of handing its whole self to the metrics.

**Why only "worth exploring".** One file listing every event ID is also what keeps the table in [refactoring-spec.md §5.6](refactoring-spec.md) honest, and NFR-07 ties those IDs to existing monitoring. The constants would have to stay shared, and the win is then smaller than it first looks.

---

## 5. Make startup a module, not a script

**Where:** `src/PanRaProxy/Program.cs:9-12, 37-39, 49-59` · `Options/StartupValidation.cs` · `Diagnostics/DiagnosticsRegistration.EnsureEventLog`.

**Problem.** The behaviour an administrator meets first has no test: the `--debug` / `--help` / `--version` switches and their removal from the arguments passed to configuration, the Event Log message when upstream's source still owns the name, and exit code 1 with event 3106 for an invalid configuration. It is all in top-level statements, so nothing reaches it; the exit codes in [install.md](install.md) are checked by hand.

**Solution.** One interface: arguments in, exit code out. `Program.cs` becomes a single call.

**What improves.** The documented exit codes become assertions. `--help` and `--version` get pinned. The Event Log fallback path can be tested with a fake instead of a machine.

---

## 6. Split failover from submission

**Where:** `src/PanRaProxy/Firewall/FirewallClient.cs`.

**Problem.** None observed. The module is already deep: one method, with the uid-message, the `X-PAN-KEY` header, the per-attempt timeout, ordered failover and the response parsing behind it, and its tests go through that one interface.

**Solution, if ever.** Separate the failover policy (order, stickiness, what counts as unreachable) from one attempt against one Firewall.

**Why speculative.** There is one adapter, so the seam would be hypothetical. Revisit if a second policy appears: sending to both HA peers, or a health check. See also the open question in [deployment.md](deployment.md) about how the passive HA peer answers.

---

## Not in scope here

- The net462 code (`src/Lithnet.Pan.RAProxy`), kept only for the parallel test.
- `tools/e2e/live-replay.py`: its expectation is deliberately a second implementation of the rules, so duplication there is the point.
- Anything ADR 0001–0003 settled.
