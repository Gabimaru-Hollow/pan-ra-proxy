# Architecture review — 2026-09-25

Six **candidates, not decisions**: places where the .NET 8 code is harder to change or to test than it needs to be, with the file and line that makes each one concrete. None of them contradicts [ADR 0001](adr/0001-batch-with-hard-timer.md), [0002](adr/0002-dotnet8-refactor-over-go.md) or [0003](adr/0003-secrets-as-protected-files-provided-after-install.md), and none changes what the Proxy does: the terms are from [CONTEXT.md](../CONTEXT.md), the requirements from [refactoring-spec.md](refactoring-spec.md).

Reviewed at `ed83069` on `refactor/net8-fork`, with 150 tests green. The review looked at what changed recently (`Mappings`, `Options`, `Diagnostics`, `Program.cs`), not at the whole repository, and skipped the net462 code, which is kept only until the parallel test with Vector is signed off.

| # | Candidate | Strength |
|---|---|---|
| 1 | [One composition root](#1-one-composition-root) | Done |
| 2 | [Canonical Username as its own module](#2-canonical-username-as-its-own-module) | Done |
| 3 | [A domain rule that validates itself](#3-a-domain-rule-that-validates-itself) | Done |
| 4 | [Break the Diagnostics hub](#4-break-the-diagnostics-hub) | Rejected ([ADR 0005](adr/0005-log-messages-stay-in-one-file.md)) |
| 5 | [Make startup a module, not a script](#5-make-startup-a-module-not-a-script) | Worth exploring |
| 6 | [Split failover from submission](#6-split-failover-from-submission) | Speculative |
| 7–11 | [Second pass: fragilities](#second-pass-fragilities) | 7–10 fixed; 11 mostly |

Suggested order: 1 first, because it is cheap and protects the evidence the rest rests on; then 2 and 3 together, because they are the same change seen from two sides. The [validation agenda](#validation-agenda) at the end says what each candidate has to answer before it is worth doing.

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

**Outcome.** Done: `ProxyRegistration.AddPanRaProxy` declares the shared adapters once, and the five per-area registrations are now `internal`. The discussion corrected two statements above:

- *"The order the five calls run in is load-bearing"* was wrong. Every `TryAdd` of a type registered the same implementation, so reordering the five changed nothing. What was load-bearing was where the test put its replacements: `SecretLookup` and `ProcessExit` had to go before the five (the first `TryAdd` wins), `FirewallClient` after them (the last `Add` wins). Now every replacement comes after `AddPanRaProxy`, through `services.Replace`.
- *"Replace the `HttpMessageHandler`"* wasn't possible: the handler isn't a service, and it is built inline with the certificate trust. The test still replaces `FirewallClient`, as before, which adds no seam to production code.

The deciding question (a real cost, or already paid?) was settled by the alternative collapsing: a test comparing the two graphs can't reach `Program`'s, which is made of top-level statements. The same change also closed [finding 8](#8-configuration-problems-that-bypass-event-3106), and the first build after it showed the old drift again: the hand-assembled test graph failed as soon as `TimeProvider` moved to the root.

---

## 2. Canonical Username as its own module

**Where:** `src/PanRaProxy/Mappings/MappingDecider.cs:41-48, 93-129` (`ToCanonicalUsername`, the private `DomainRule` record, `CreateRegex`) · `Mappings/NameTranslation.cs` · `Mappings/CachingNameTranslator.cs` · `tests/PanRaProxy.Tests/Mappings/CanonicalUsernameTests.cs`.

**Problem.** The Mapping decision module holds two subjects. One is what an Accounting-Request means: status type, Placeholder IPs, Logout-on-Stop, Timeout. The other is how a received username becomes the Canonical Username: Username Rewrites, the ordered domain rules, `${user}` templates, the directory lookup and its cache. The second is about half the module, it isn't in its name, and it is the part that changes most: it changed twice in the week of 2026-09-22 (multiple UPN suffixes, then rules).

The test surface shows it. `CanonicalUsernameTests` has to build an `AccountingRequest` with a status type and an IP address to ask what a username becomes, and reads the answer back out of a `MappingChange`.

**Solution.** A module whose interface is that question: a raw `User-Name` in, a Canonical Username out. Behind it the rewrites, the rules, the templates and the lookup; the Mapping decision module takes it as one dependency.

**What improves.** Tests ask the real question. Rule changes stay in one module. The decision module shrinks to the concept it is named after. The lookup (Windows-only, cached) sits behind an internal seam instead of being visible in the decision code.

**Outcome.** Done, together with 3 and [finding 9](#9-a-directory-lookup-can-stall-the-listener). `Mappings/CanonicalUsername.cs` holds `CanonicalUsernameResolver` (a raw User-Name in, a Canonical Username out), and `MappingDecider` takes it as one dependency. `CanonicalUsernameTests` asks the resolver directly. The deciding question was answered on 2026-09-25: the rules will keep moving, because bare names, UPNs of several suffixes and NT4 names all reach the Proxy, and `Lookup` is needed in production.

The interface is plain `string → string`. A result that says which rule matched, so the Proxy could count the Mappings that aren't in NT4 form, is deferred. What would bring it back: the replay leaves 13 of 40 Mappings in UPN form (the suffixes no rule covers), so if production shows the same, that number is worth watching.

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

**Outcome.** Done. `CanonicalUsernameRules.TryCreate` is the only definition of a valid rule. A `DomainRule` validates itself and applies itself (`DomainRule.Apply`). The Mappings module registers `CanonicalUsernameRulesValidator`, so the rule failures still reach event 3106 with the other UserId settings, and `ValidateDomainRule` is gone. Every regex in the settings now comes from `OptionsChecks.CreateRegex`, so a pattern is validated with the same options it runs with.

The regexes are still compiled twice at startup, once by the validator and once for the singleton. That is accepted: the cost is negligible, the configuration is never reloaded without a restart, and what mattered was having one statement of the rules.

---

## 4. Break the Diagnostics hub

**Where:** `src/PanRaProxy/Diagnostics/Log.cs` (93 lines, `using` of `Radius`, `Mappings`, `Firewall`) · `Diagnostics/ProxyMetrics.cs:24, 36-37`.

**Problem.** Every module depends on Diagnostics, and Diagnostics depends on every module: `Log` takes `AccountingRequest`, `DiscardReason`, `DropReason` and `Uri firewall`, and `ProxyMetrics` takes `MappingBatcher` in its constructor to observe queue depth and drops. The dependency runs both ways, and adding one log line to the Firewall module means editing a file shared with the listener.

**Solution.** Each module keeps its own log messages and counters next to the code that raises them. Diagnostics keeps what is genuinely shared: the event-ID constants, the file log, the Event Log settings and the meter. The Batch module exposes a small queue snapshot instead of handing its whole self to the metrics.

**Why only "worth exploring".** One file listing every event ID looked like what kept the event IDs honest, and NFR-07 tied those IDs to existing monitoring. The constants would have to stay shared, and the win is then smaller than it first looks.

**Outcome.** Rejected, recorded in [ADR 0005](adr/0005-log-messages-stay-in-one-file.md). The table the candidate was protecting didn't exist: the spec has no §5.6, even though four documents cited it. And no monitoring depends on upstream's IDs, because the upstream Proxy never ran in this deployment (the existing pipeline is Vector reading NPS logs). NFR-07 was amended, a registry would be YAGNI, and the cycle sits inside one assembly and has cost nothing. Messages can move next to their module when that module is being changed anyway.

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

## Second pass: fragilities

A second, deeper pass over the whole of `src/PanRaProxy`, looking for fragile choices and checking it against SOLID, DRY, KISS and YAGNI. What held up: the RADIUS parsing is pure, the closed result types (`PacketVerdict`, `MappingDecision`, `SubmissionResult`) are used as intended, `TimeProvider` is injected everywhere, the Batch timer is simple, and certificate trust is scoped to the Firewall client.

### 7. A failed log roll ended the process

**Fixed.** `FileLogWriter` caught only `IOException` on its own thread. An `UnauthorizedAccessException` while opening the next file ended the process (reproduced), and an `IOException` there left the writer on a closed stream, so the next line did the same. Now a line that can't be written is counted, the next line tries to open a file again, and the count is written as soon as one opens. `FileLoggingTests.A_roll_that_cannot_open_the_next_file_does_not_stop_the_proxy` pins it.

### 8. Configuration problems that bypass event 3106

**Fixed, with candidate 1.** [install.md](install.md) promises exit code 1 and event 3106 for an invalid configuration, but three problems only appeared in constructors when the host started: a RADIUS Client that doesn't resolve, one address with two secrets, and a `CaFile` that exists but can't be loaded. They surfaced as an unhandled exception instead. `StartupValidation` now builds the hosted services once the options are valid and reports a construction failure in 3106, while the DNS and certificate errors are turned into messages that name the setting. Construction stops at the first problem, so these come one at a time.

### 9. A directory lookup can stall the listener

**Fixed.** `IAccountingRequestSink` says "Must not block": the listener calls it inline. With a `Lookup` rule, a cache miss calls `TranslateNameW` synchronously (`Mappings/NameTranslation.cs`) on that path. With a slow or unreachable domain controller, the receive loop waits: the current packet is already acknowledged, but the next datagrams queue in the socket buffer. The likely moment is the burst of Interim-Updates after a domain controller outage.

`Lookup` is needed in production (2026-09-25). Clients authenticate with bare names, UPNs of several suffixes, and NT4 names. NPS resolves them itself because its host is domain-joined, but accounting reaches the Proxy from the NAS side, and User-ID needs one Canonical Username (the sAMAccountName form). So this finding has to be fixed. The direction is to resolve the Canonical Username off the listener's path, which is also where [candidate 2](#2-canonical-username-as-its-own-module) would put it.

Three placements were weighed:
- a lookup inline with a time limit, which would still pause reception on every cache miss;
- resolving inside the Batch, which would stretch the ADR 0001 timer and move deduplication into the batcher;
- a decision worker, which was chosen.

The listener now only queues the accepted request in `AccountingRequestQueue`. `MappingDecisionWorker` runs the whole Mapping decision, lookup included, on its own and feeds the Batch, which is unchanged. The request queue follows the Batch queue's rules (NFR-03): capacity `UserId:QueueCapacity`, and when full the oldest request is dropped and counted. It shows up as `panraproxy.requests.queue.depth` and `panraproxy.requests.queue.dropped`. A slow domain controller now delays Logins, never reception or acknowledgements. `MappingDecisionWorkerTests` pins both properties: the listener doesn't wait, and a failing decision doesn't stop the worker.

### 10. `Replace` means two different things

**Fixed in part.** A Username Rewrite applies `Regex.Replace`, which substitutes the matched part. A domain rule's `Replace` applies `Match.Result`, where the template is the whole result. With `Match: "@domain\.local$"` and `Replace: ""`, the rewrite turns `mrossi@domain.local` into `mrossi`, but the rule turns it into an empty string. The validator accepts it (it checks only for null), so the Firewall would receive a Login with an empty username. Username Rewrites stay, confirmed needed on 2026-09-25, so the fix is to reject an empty result and to make the difference visible in the names or the documentation. It belongs with candidates 2 and 3.

Now an empty `Nt4Domain`, `Replace` or `Lookup` on a domain rule is rejected at startup (3106). The two meanings of `Replace` remain, documented on the options. Renaming one of them would break existing site settings for a small gain.

### 11. Smaller points

**Open, low priority.**
- **DRY.** *Fixed with candidate 3.* The validator compiled regexes with `IgnoreCase`, while the decision module used `IgnoreCase | CultureInvariant`.
- **SRP / discoverability.** *Half fixed.* `MappingDecisionSink` became `MappingDecisionWorker`, in a file of its own. `BatchSender` still lives in `FirewallRegistration.cs`.
- **DIP.** `Func<string, IPAddress[]>` is a DI key. A raw `Func` is an ambiguous key: any other `Func` of the same shape would collide. The other adapters are named delegates (`SecretLookup`, `ProcessExit`).
- **KISS / YAGNI.** *Done.* The rolling file logger was 265 lines of our own code, and it held finding 7. Serilog's file sink now writes the files, behind `ILogger` and known only to `Diagnostics/FileLogging.cs` ([ADR 0004](adr/0004-file-log-through-serilog-behind-ilogger.md)).

---

## Validation agenda

These candidates were written by the author of the code they criticise, so the first job of the
discussion is to attack them, not to plan them. For each one: the question that decides it, what
would answer that question, and what to do with a "no".

**How to close each item.** Accepted → implement it, keep the tests green and rerun the end-to-end
replay (`--speed 30`, about 30 s) to check the observable behaviour didn't move. Rejected for a
reason a future reader would need → record an ADR, so the next review doesn't propose it again.
Deferred → say so here, with what would bring it back.

**Order is not free.** Candidate 1 changes the composition, which is what the end-to-end test uses as
its safety net: do it first, while the net is still the one that has been proving the code. Candidates
2 and 3 touch the same area and should be decided together. Candidate 5 is independent. Candidate 4
deserves a decision before any code, because its cost is documentary.

**A caution about the evidence.** The git history is ten commits old, so "how often has this file
changed" proves nothing yet. The real evidence is in the week of 2026-09-22: the username rules were
reworked twice (`c80b458`), and the end-to-end test had to have `AddProxyDiagnostics` added by hand
when the metrics arrived. Where a candidate rests on churn, say whether that churn is expected to
continue.

| # | The question that decides it | What would answer it | If the answer is no |
|---|---|---|---|
| 1 | Is the duplicated composition a real cost, or a one-off already paid? | Whether the registrations are expected to keep changing (metrics, a config-check command, a replay tool), and whether a test asserting "the test's graph equals `Program`'s" would be enough instead | Probably no ADR: the reason is ephemeral |
| 2 | Are the username rules still going to move? | The AD spot-check on `name.surname`: if the convention holds everywhere, the rules freeze and the split buys less. Also decide the interface: plain `string → string`, or a result that says which rule matched, which would let the Proxy count non-canonical Mappings | ADR worth writing: "the Canonical Username rules stay inside the Mapping decision because …" |
| 3 | Should an invalid rule be unrepresentable, or is validation-at-startup enough? | Whether configuration will ever be reloaded without a restart. If it never is, compiling once at startup may already be sufficient, and the duplication is only the `user`-group check | ADR if rejected on the "two statements of the same rule are fine because …" ground |
| 4 | Does the event-ID table need to live in one file? | How NFR-07 would be kept honest with the messages spread across modules: a shared constants file, or a test that reads the IDs and compares them with a table | Rejected: ADR 0005 |
| 5 | Which startup behaviours must be pinned by a test? | The list of things an administrator depends on: exit codes, `--help`, `--version`, the Event Log fallback message. If the console surface is going to grow (say a `--check-config`), the module is justified now rather than later | No ADR needed: it is a question of when, not whether |
| 6 | Is a second failover policy coming? | The open question in [deployment.md](deployment.md): how the passive HA peer answers a User-ID call. If it answers with an API error, the failover rule itself has to change, and that is the moment to reconsider | ADR: "failover stays inside the Firewall submission module until a second policy exists" |

**Two questions that sit above the list.** First: is there a candidate missing, something that has actually
hurt while working in this code and isn't here? Second: which of these would you not want done even if
free, because it would make the code harder to read for whoever operates it?

## Not in scope here

- The net462 code (`src/Lithnet.Pan.RAProxy`), kept only for the parallel test.
- `tools/e2e/live-replay.py`: its expectation is deliberately a second implementation of the rules, so duplication there is the point.
- Anything ADR 0001–0003 settled.
