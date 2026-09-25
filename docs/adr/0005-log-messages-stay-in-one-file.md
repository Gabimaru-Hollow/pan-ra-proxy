# Log messages stay in one file, without an event-ID registry

`Diagnostics/Log.cs` keeps every `[LoggerMessage]` of the Proxy, and `Diagnostics/ProxyMetrics.cs` every instrument, even though this makes Diagnostics depend on the types of every module and every module depend on Diagnostics (architecture review, candidate 4). No separate table of event IDs is maintained next to the code, and no test compares the two: `Log.cs` is the list.

## Considered Options

- **Messages and counters next to the module that raises them**, with only the event-ID constants shared. It removes a dependency cycle that sits inside one assembly and has cost nothing so far, in exchange for churn across every module. Nothing forbids moving a message when its module is being changed anyway.
- **A documented event-ID table, kept honest by a test**: it would protect monitoring built on upstream's event IDs (the original NFR-07). No such monitoring exists, because the upstream Proxy never ran in this deployment: the existing pipeline is Vector reading NPS logs. So the registry would guard a consumer that doesn't exist. NFR-07 was amended to say so.

## Consequences

- IDs are stable from this version on. They follow upstream's numbering where the event is the same, and upstream's 3003 (an unreadable Firewall response) is reported as 3002.
- If external monitoring is ever built on these IDs, that is the moment to pin them with a test.
