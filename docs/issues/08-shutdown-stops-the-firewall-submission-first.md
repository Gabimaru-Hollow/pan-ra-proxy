# 8. Shutdown stops the Firewall submission first

**Severity:** Low · **Status:** Verified by reading

**What.** Hosted services stop in the reverse of their registration order. `AddPanRaProxy` registers the Mappings module, then RADIUS, then the Firewall. So on stop:
1. `BatchSender` stops first;
2. the listener stops next, but until then it keeps acknowledging requests whose Mappings will never be sent;
3. the worker stops last.

This is accepted by P3-4 (no persistence, Interim-Updates rebuild), and it costs a few seconds of accounting per stop.

**Possible change.** Register the Firewall module first and RADIUS last, so the listener stops before anything else.

[← All known issues](README.md)
