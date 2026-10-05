---
status: accepted
---

# The repository holds only the .NET 8 Proxy

This repository is the .NET 8 Proxy (`src/PanRaProxy`), with its installer, tests and tools. [lithnet/pan-ra-proxy](https://github.com/lithnet/pan-ra-proxy) is where it came from, not a codebase kept alongside it. The upstream net462 code, its WiX v3 installer, its solution, its Azure pipeline and its GitHub settings were removed on 2026-10-05. What links the two projects now is the attribution in the README, the upstream copyright in `LICENSE.txt` (MIT requires it, and code such as Name Translation was ported), and the review of the upstream code in [refactoring-spec.md](../refactoring-spec.md).

The net462 code had been kept, with the four P1 fixes (spec §3.3), to run alongside the Vector pipeline while the .NET 8 Proxy was being written. That role passed to the .NET 8 Proxy itself: `--dry-run` takes the production accounting and logs each Login and Logout without a Firewall. Since then the two codebases had diverged in every decision that matters (ADR 0001, 0004, 0006, 0007), so maintaining the old one would have meant fixing code nobody runs. Keeping it also had a cost: two solutions and two installers, with service names and Event Log sources that differ only in case.

## Considered Options

- **Keep the net462 code until the parallel test is signed off** (the earlier plan). Rejected: the parallel test no longer needs it.
- **Tag the P1-fixed net462 version before removing it.** Rejected: nobody intends to run it. It stays reachable in the history, at the commit that applied the P1 fixes.

## Consequences

- There is no in-repo fallback to the upstream Proxy. If the .NET 8 Proxy fails its production trial, the fallback is the Vector pipeline, which keeps running throughout.
- The file and line references in [refactoring-spec.md](../refactoring-spec.md) §2–3 point at the upstream code at the fork baseline `049992e`, which is still in the history, not at anything in the current tree.
- [install.md](../install.md) still describes upgrading from, and not coexisting with, an *installed* upstream service. That is about machines, not code, and stays as long as such an install can exist.
