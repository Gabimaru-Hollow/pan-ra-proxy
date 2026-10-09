# 3. Exit code and timing of a listener failure under the service

**Severity:** Low · **Status:** Unverified as a service

**What.** When the listener can't continue (for example the UDP port is taken), it calls `Environment.Exit(1)` so Windows Service recovery restarts the Proxy (NFR-08). In .NET 8, the part of `ExecuteAsync` before its first `await` runs inside host start. That includes the bind, so `Environment.Exit` runs while the host is starting.

- **In a console:** verified on 2026-09-28. With the port taken, the Proxy exits with code 1 after 1.8 s, with event 3103.
- **As a service:** not verified. The service lifetime handles process exit differently, and the recovery actions rely on a non-zero exit.

**To verify.** Once the service is installed, take the port with another process, start the service, and check the service's exit code (`sc query PanRaProxy`), the time it takes to stop, and that recovery restarts it.

[← All known issues](README.md)
