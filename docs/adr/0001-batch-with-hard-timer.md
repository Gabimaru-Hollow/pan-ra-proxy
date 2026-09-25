# Batch Logins and Logouts within a hard-timer window

Mappings must reach the Firewall as soon as possible after a client gets an IP, which argues for one API call per accounting packet. We keep a short Batch instead, closed by a hard timer that starts at the first packet (`BatchWindowMs`, configurable) or when `BatchSize` is reached. The alternative, a sliding wait that restarts on every packet, is what upstream does, and under a steady trickle of accounting it can hold a Batch open for up to `BatchSize × wait` (about 10 s with 200 × 50 ms).

## Considered Options

- **One call per packet**: lowest latency, but a Roam's Stop and Start can no longer cancel out within a Batch, and a Controller reboot turns hundreds of reconnects into hundreds of HTTPS calls.
- **Sliding wait (upstream)**: groups bursts well but gives no upper bound on latency.
- **Hard-timer window (chosen)**: bounded latency of at most `BatchWindowMs` plus one API round trip, while keeping Stop→Start collapse and burst grouping.
