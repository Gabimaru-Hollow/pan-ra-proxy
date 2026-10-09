# 4. Event Log detection under the service account

**Severity:** Low · **Status:** Unverified

**What.** `DiagnosticsRegistration.EventLogProblem` reads the registry to decide whether the Event Log can be used. From a user session it works as expected: verified with the source missing, and with the source under the Application log. Under `NT SERVICE\PanRaProxy`, a virtual account with no administrator rights, it has never run, because the MSI has never been installed. If the check fails there, the service runs without the Event Log and says so (event 2002). The Proxy keeps running, but the monitoring destination is lost without anyone noticing.

**To verify.** Install with the MSI, start the service, and check that event 4004 (Listening) and a Warning appear in the `PanRaProxy` log.

[← All known issues](README.md)
