# Known issues

Found in the code review of 2026-09-28, which looked for edge cases, leaks, races and unhandled exceptions in `src/PanRaProxy`. This folder lists what the review could **not** verify, and what needs a decision rather than a fix. Verified defects were fixed in the same change and are listed at the end.

Each issue is a file of its own and says how it was found:
- **Verified**: reproduced, or certain from the code path.
- **Unverified**: from reading the code and the framework's behaviour; it needs the environment described to confirm.

When an entry is closed, move it to *Resolved* with the commit, or to an ADR if the answer is "accepted".

| # | Issue | Severity | Status |
|---|---|---|---|
| 1 | [An Event Log write that fails can stop the Proxy](01-an-event-log-write-that-fails-can-stop-the-proxy.md) | Medium | Unverified |
| 2 | [A UDP "network reset" ends the listener](02-a-udp-network-reset-ends-the-listener.md) | Low | Unverified |
| 3 | [Exit code and timing of a listener failure under the service](03-exit-code-and-timing-of-a-listener-failure-under-the-service.md) | Low | Unverified as a service |
| 4 | [Event Log detection under the service account](04-event-log-detection-under-the-service-account.md) | Low | Unverified |
| 5 | [Log flooding from one sender or one Batch](05-log-flooding-from-one-sender-or-one-batch.md) | Low–Medium | Verified; needs a decision |
| 6 | [The Name Translation caches never shrink](06-the-name-translation-caches-never-shrink.md) | Low | Verified; needs a decision |
| 7 | [A hanging directory lookup stalls the Mapping decision](07-a-hanging-directory-lookup-stalls-the-mapping-decision.md) | Low | Verified by reading |
| 8 | [Shutdown stops the Firewall submission first](08-shutdown-stops-the-firewall-submission-first.md) | Low | Verified by reading |
| 9 | [A setting value that starts with a dash](09-a-setting-value-that-starts-with-a-dash.md) | Low | Verified by reading |
| 10 | [What `--set-secret` does to the machine](10-what-set-secret-does-to-the-machine.md) | Medium | Unverified: needs an elevated run on an install |
| 11 | [How wide a Logout without `blockstart` reaches](11-how-wide-a-logout-without-blockstart-reaches.md) | Low (only with Logout-on-Stop on) | Unverified |

---

## Resolved in the review

| Issue | Fix |
|---|---|
| A username with a control character (`\u0001`, `\u0000`) failed the XML serialization of the uid-message. The whole Batch was dropped, and again at every Interim-Update, for everyone who shared a Batch with that user. Reproduced. | `MappingDecider` drops such a request, and one whose Canonical Username is empty, as `DropReason.UnusableUsername`. It is logged as 4005 at Debug and counted in `panraproxy.mappings.requests.dropped`. |
| A site `appsettings.json` that isn't valid JSON crashed the Proxy with an unhandled exception, `--check-config` included. Reproduced. | `ProxyStartup` reports it and exits 1: on the console, and as a service in the Event Log (3106) when the Proxy's own source exists. |
| A value that doesn't convert (`Radius:Port=abc`) crashed the Proxy instead of producing event 3106. Reproduced. An unreadable secret file would take the same path. | `StartupValidation` reports any binding or validation exception in 3106. |
