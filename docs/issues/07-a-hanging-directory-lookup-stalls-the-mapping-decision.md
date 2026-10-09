# 7. A hanging directory lookup stalls the Mapping decision

**Severity:** Low · **Status:** Verified by reading

**What.** `TranslateNameW` is a synchronous native call with no timeout. The worker keeps it off the listener (finding 9 of the architecture review), but while it hangs:
- no Mapping is decided, and Logins wait in the request queue;
- after `UserId:QueueCapacity` requests the oldest are dropped (NFR-03, counted in `panraproxy.requests.queue.dropped`);
- stopping the service waits up to `HostOptions.ShutdownTimeout` (30 s) before giving up on the worker.

This is how it's designed rather than a defect. It's listed because a domain controller outage turns it from theory into minutes of missing Mappings.

[← All known issues](README.md)
