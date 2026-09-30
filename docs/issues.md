# Known issues

Found in the code review of 2026-09-28, which looked for edge cases, leaks, races and unhandled exceptions in `src/PanRaProxy`. This file lists what the review could **not** verify, and what needs a decision rather than a fix. Verified defects were fixed in the same change and are listed at the end.

Each entry says how it was found:
- **Verified**: reproduced, or certain from the code path.
- **Unverified**: from reading the code and the framework's behaviour; it needs the environment described to confirm.

When an entry is closed, move it to *Resolved* with the commit, or to an ADR if the answer is "accepted".

| # | Issue | Severity | Status |
|---|---|---|---|
| 1 | [An Event Log write that fails can stop the Proxy](#1-an-event-log-write-that-fails-can-stop-the-proxy) | Medium | Unverified |
| 2 | [A UDP "network reset" ends the listener](#2-a-udp-network-reset-ends-the-listener) | Low | Unverified |
| 3 | [Exit code and timing of a listener failure under the service](#3-exit-code-and-timing-of-a-listener-failure-under-the-service) | Low | Unverified as a service |
| 4 | [Event Log detection under the service account](#4-event-log-detection-under-the-service-account) | Low | Unverified |
| 5 | [Log flooding from one sender or one Batch](#5-log-flooding-from-one-sender-or-one-batch) | Low–Medium | Verified; needs a decision |
| 6 | [The Name Translation caches never shrink](#6-the-name-translation-caches-never-shrink) | Low | Verified; needs a decision |
| 7 | [A hanging directory lookup stalls the Mapping decision](#7-a-hanging-directory-lookup-stalls-the-mapping-decision) | Low | Verified by reading |
| 8 | [Shutdown stops the Firewall submission first](#8-shutdown-stops-the-firewall-submission-first) | Low | Verified by reading |
| 9 | [A setting value that starts with a dash](#9-a-setting-value-that-starts-with-a-dash) | Low | Verified by reading |
| 10 | [What `--set-secret` does to the machine](#10-what---set-secret-does-to-the-machine) | Medium | Unverified: needs an elevated run on an install |

---

## 1. An Event Log write that fails can stop the Proxy

**What.** `Microsoft.Extensions.Logging` calls every provider for each entry. If a provider throws, it collects the exceptions and **rethrows them to the caller** as an `AggregateException` ("An error occurred while writing to logger(s)"). The Event Log provider catches only `SecurityException`. A failed `EventLog.WriteEvent` for any other reason therefore reaches the code that logged. The obvious reason is "The event log file is full", when a GPO sets the log's retention to *Do not overwrite*.

**Where it would hurt.**
- `AccountingListener`: `Log.Listening` sits outside the inner try, so its exception reaches the outer catch. That catch logs `ListenerFailed`, which throws again, so the listener ends. With .NET 8's default (`BackgroundServiceExceptionBehavior.StopHost`) the Proxy stops.
- The same applies to any `Log.*` call inside a `catch` block anywhere.

Only entries at Warning and above reach the Event Log, so the trigger is any warning while the log refuses writes.

**To verify.** Set the `PanRaProxy` log to *Do not overwrite* with a small maximum size, fill it, then start the Proxy with an unknown RADIUS Client sending (event 3101, Warning).

**Possible fix.** Wrap the Event Log provider in a decorator that swallows and counts its own failures, the way Serilog's sink already behaves for the files.

## 2. A UDP "network reset" ends the listener

**What.** On Windows a UDP socket reports two kinds of ICMP errors on the next receive:
- *port unreachable*, as `ConnectionReset`. The listener turns it off (`SIO_UDP_CONNRESET`) and also ignores it.
- *TTL expired*, as `NetworkReset` (`SIO_UDP_NETRESET`). It is neither turned off nor caught, so it reaches the outer catch, and the listener exits the process (event 3103). Windows then restarts the service 60 s later, and the accounting of that minute is lost.

**To verify.** Only a routing problem between the Proxy and the Forwarding Server produces TTL-expired messages for the Accounting-Responses. It is rare on a LAN.

**Possible fix.** Treat `SocketError.NetworkReset` like `ConnectionReset`, and turn it off with the matching IOControl.

## 3. Exit code and timing of a listener failure under the service

**What.** When the listener can't continue (for example the UDP port is taken), it calls `Environment.Exit(1)` so Windows Service recovery restarts the Proxy (NFR-08). In .NET 8, the part of `ExecuteAsync` before its first `await` runs inside host start. That includes the bind, so `Environment.Exit` runs while the host is starting.

- **In a console:** verified on 2026-09-28. With the port taken, the Proxy exits with code 1 after 1.8 s, with event 3103.
- **As a service:** not verified. The service lifetime handles process exit differently, and the recovery actions rely on a non-zero exit.

**To verify.** Once the service is installed, take the port with another process, start the service, and check the service's exit code (`sc query PanRaProxy`), the time it takes to stop, and that recovery restarts it.

## 4. Event Log detection under the service account

**What.** `DiagnosticsRegistration.EventLogProblem` reads the registry to decide whether the Event Log can be used. From a user session it works as expected: verified with the source missing, and with the source under the Application log. Under `NT SERVICE\PanRaProxy`, a virtual account with no administrator rights, it has never run, because the MSI has never been installed. If the check fails there, the service runs without the Event Log and says so (event 2002). The Proxy keeps running, but the monitoring destination is lost without anyone noticing.

**To verify.** Install with the MSI, start the service, and check that event 4004 (Listening) and a Warning appear in the `PanRaProxy` log.

## 5. Log flooding from one sender or one Batch

**What.** Some events are written once per occurrence, with no limit:
- **3101**, Warning: once per packet from a sender that isn't a RADIUS Client. By default the Windows Firewall rule accepts any sender (`RADIUS_CLIENTS` empty). A misconfigured NAS, or anyone on the network, can therefore write one Event Log entry per datagram.
- **3007/3008**, Error: once per Login or Logout the Firewall rejects. A persistent rejection repeats at every Interim-Update, for every entry.

The files are bounded (14 × 16 MB). The Event Log overwrites its oldest entries, which are exactly the ones that matter after an incident.

**To decide.** One summary line per sender per interval (for example "1234 packets from X in the last minute"), or accept it and require `RADIUS_CLIENTS` at install time.

## 6. The Name Translation caches never shrink

**What.** `CachingNameTranslator` overwrites an expired entry when the same UPN comes back, but never removes entries whose UPN doesn't come back. The same holds for the unknown-domain list in `WindowsNameTranslator`. Growth is bounded by the distinct UPNs seen since the Proxy started, at about 100 bytes each: 100,000 distinct UPNs is about 10 MB. It only applies when a `Lookup` rule is configured.

**To decide.** Accept it, since the service restarts on upgrades, or prune expired entries when the cache passes a size.

## 7. A hanging directory lookup stalls the Mapping decision

**What.** `TranslateNameW` is a synchronous native call with no timeout. The worker keeps it off the listener (finding 9 of the architecture review), but while it hangs:
- no Mapping is decided, and Logins wait in the request queue;
- after `UserId:QueueCapacity` requests the oldest are dropped (NFR-03, counted in `panraproxy.requests.queue.dropped`);
- stopping the service waits up to `HostOptions.ShutdownTimeout` (30 s) before giving up on the worker.

This is how it's designed rather than a defect. It's listed because a domain controller outage turns it from theory into minutes of missing Mappings.

## 8. Shutdown stops the Firewall submission first

**What.** Hosted services stop in the reverse of their registration order. `AddPanRaProxy` registers the Mappings module, then RADIUS, then the Firewall. So on stop:
1. `BatchSender` stops first;
2. the listener stops next, but until then it keeps acknowledging requests whose Mappings will never be sent;
3. the worker stops last.

This is accepted by P3-4 (no persistence, Interim-Updates rebuild), and it costs a few seconds of accounting per stop.

**Possible change.** Register the Firewall module first and RADIUS last, so the listener stops before anything else.

## 9. A setting value that starts with a dash

**What.** `CommandLine` treats any argument that starts with `-` or `/` and has no `:` as a switch. In the two-token form, a value that starts with a dash is taken as an unknown option, and the Proxy exits with 2. For example, in `--UserId:BatchWindowMs -1` the `-1` is rejected. The `=` form works: `--UserId:BatchWindowMs=-1`.

**To decide.** Document "use `--Key=value`" (the usage already shows only that form), or let a dash argument through when the previous one was a setting without a value.

## 10. What `--set-secret` does to the machine

**What.** The tests cover the command's logic (reading, encryption, the stored format, exit codes). They replace its three effects on the machine with fakes, because a test session isn't elevated and must not change ACLs or services:

- **The ACL** (`SecretCommand.ProtectDirectory`):
  - inheritance from ProgramData removed;
  - full control for SYSTEM and Administrators;
  - read for `NT SERVICE\PanRaProxy`, resolved by name (if the service isn't installed, the name doesn't resolve and a note says so).
- **The service restart** (`SecretCommand.RestartServiceIfRunning`): stop, then start, with 30 s for each.
- **The prompt** (`SecretCommand.ReadFromConsole`): no echo, the value typed twice, or piped from standard input.

**To verify.** On an installed machine, from an elevated PowerShell:
1. Run `--set-secret radius` twice: once typed, once piped.
2. Check the folder's ACL (`Get-Acl $env:ProgramData\PanRaProxy\secrets | Format-List`).
3. Check that a non-elevated prompt gets exit 1 with the "elevated prompt" message.
4. Check that the running service restarts and logs 4004 again.
5. Check that `--check-config` from a non-elevated prompt reports 2003 rather than "not set".

---

## Resolved in the review

| Issue | Fix |
|---|---|
| A username with a control character (`\u0001`, `\u0000`) failed the XML serialization of the uid-message. The whole Batch was dropped, and again at every Interim-Update, for everyone who shared a Batch with that user. Reproduced. | `MappingDecider` drops such a request, and one whose Canonical Username is empty, as `DropReason.UnusableUsername`. It is logged as 4005 at Debug and counted in `panraproxy.mappings.requests.dropped`. |
| A site `appsettings.json` that isn't valid JSON crashed the Proxy with an unhandled exception, `--check-config` included. Reproduced. | `ProxyStartup` reports it and exits 1: on the console, and as a service in the Event Log (3106) when the Proxy's own source exists. |
| A value that doesn't convert (`Radius:Port=abc`) crashed the Proxy instead of producing event 3106. Reproduced. An unreadable secret file would take the same path. | `StartupValidation` reports any binding or validation exception in 3106. |
