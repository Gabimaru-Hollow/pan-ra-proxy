# The file log is written by Serilog, behind ILogger

The Proxy's code logs only through `Microsoft.Extensions.Logging`: `ILogger<T>` and the `[LoggerMessage]` methods in `Diagnostics/Log.cs`. Serilog is used for one job only: writing the rolling log files, through `Serilog.Sinks.File` behind a provider (`Diagnostics/FileLogging.cs`). That file is the only one that knows Serilog. The provider keeps the `File` alias, so the settings stay under `Logging:File`, and level filtering stays the standard `Logging:File:LogLevel`. The Event Log and the console keep their Microsoft providers.

## Considered Options

- **Our own file writer**, as before: 265 lines covering a background thread, rolling and pruning. It held the defect that let a failed roll end the process ([specs.md](../specs.md#design-notes), *Files are written by Serilog*). Rolling files are a solved problem, and the code wasn't about the Proxy's domain.
- **Serilog as the logging API** (`Log.Logger` or Serilog's `ILogger` in the code): it would tie every module to one library, and lose the DI-injected `ILogger` the tests use to capture event IDs.
- **Serilog for every destination** (`UseSerilog` with the Event Log sink): the Event Log sink derives the event ID from a hash of the message template unless given a custom provider, while the Microsoft Event Log provider writes the `ILogger` `EventId` as it is (NFR-07). One more thing to get right, for no gain.
- **Another library for the files** (NLog, a `Microsoft.Extensions.Logging` file provider): possible. Serilog's file sink is the most used, and since only one file knows it, swapping it later costs one file.

## Consequences

- A file that can't be opened (permissions, disk full) is retried at the next roll point and at most 30 minutes later (`RollingFileSink` in `Serilog.Sinks.File` 7.0), not on the next line as the old writer did. Meanwhile warnings and errors still reach the Event Log. Serilog never throws into the caller.
- Files roll daily and at `MaxFileSizeMb`, named `panraproxy-20260925.log`, `panraproxy-20260925_001.log`. The newest `RetainedFiles` are kept.
- Lines are written on the caller's thread, not on a background thread. At this volume (a few lines a second at Information) that is fine. `Serilog.Sinks.Async` is the remedy if it ever isn't.
- The line format is unchanged except for the Critical level, which Serilog prints as `FTL` instead of `CRT`.
- Two new dependencies: `Serilog.Extensions.Logging` and `Serilog.Sinks.File`, both included in the single-file executable.
