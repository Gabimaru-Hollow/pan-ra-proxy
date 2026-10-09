# 1. An Event Log write that fails can stop the Proxy

**Severity:** Medium · **Status:** Unverified

**What.** `Microsoft.Extensions.Logging` calls every provider for each entry. If a provider throws, it collects the exceptions and **rethrows them to the caller** as an `AggregateException` ("An error occurred while writing to logger(s)"). The Event Log provider catches only `SecurityException`. A failed `EventLog.WriteEvent` for any other reason therefore reaches the code that logged. The obvious reason is "The event log file is full", when a GPO sets the log's retention to *Do not overwrite*.

**Where it would hurt.**
- `AccountingListener`: `Log.Listening` sits outside the inner try, so its exception reaches the outer catch. That catch logs `ListenerFailed`, which throws again, so the listener ends. With .NET 8's default (`BackgroundServiceExceptionBehavior.StopHost`) the Proxy stops.
- The same applies to any `Log.*` call inside a `catch` block anywhere.

Only entries at Warning and above reach the Event Log, so the trigger is any warning while the log refuses writes.

**To verify.** Set the `PanRaProxy` log to *Do not overwrite* with a small maximum size, fill it, then start the Proxy with an unknown RADIUS Client sending (event 3101, Warning).

**Possible fix.** Wrap the Event Log provider in a decorator that swallows and counts its own failures, the way Serilog's sink already behaves for the files.

[← All known issues](README.md)
