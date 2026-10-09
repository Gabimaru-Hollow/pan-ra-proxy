# Deployment: User-ID on the WiFi VLAN via NPS accounting

How the Proxy fits the target environment: 802.1X clients on an Extreme WiNG wireless network, authenticated by NPS, with a Palo Alto Firewall segmenting the WiFi VLAN. Terms are defined in [GLOSSARY.md](../GLOSSARY.md).

## Goal

Fill the Firewall's User-ID table with `DOMAIN\user ↔ IP` Mappings for the 802.1X clients served by the WiNG Controller. Mappings must be event-driven, and the Firewall's API responses must be validated. The Proxy first runs alongside the existing Vector pipeline (which reads NPS log files) so the two sets of Mappings can be compared directly.

## Architecture

```
802.1X client ──► WiNG AP ──► VX9000-01 Controller (RADIUS proxied through the controller)
                                   │ auth + accounting
                                   ▼
                          NPS (Forwarding Server)
                            Connection Request Policy "Secure Wireless Connections"
                            Accounting: forward to remote RADIUS server group
                                   │ accounting only
                                   ▼
                          Proxy (UDP 18131)
                                   │ HTTPS POST uid-message
                                   ▼
                          Firewall User-ID XML API (active firewall, then HA peer)
```

Properties of this architecture:

- **No change to WiNG**: forwarding happens on NPS.
- **Authentication never goes through the Proxy.** If the Proxy fails, the worst outcome is NPS retransmitting accounting. WiFi service is not affected.
- **NPS keeps writing its log files**, so the Proxy and the Vector pipeline can run in parallel.

## Evidence from the logs

- The `User-Name` arrives in **three forms**. In the capture of 2026-09-24 (125 packets, 37 users): 58 bare (no domain), 47 UPN, 20 `DOMAIN\user`. The domain rules in `UserId:Domain` turn all three into the Canonical Username. Historic note: the `User-Name` sent by the NAS is not always `DOMAIN\user`. The Proxy sees only this RADIUS attribute, not the `SAM-Account-Name` that NPS resolves and writes to its log. Clients that authenticate with `user@domain.local` arrive as UPN and need a Username Rewrite to reach the Canonical Username.
- In Access-Accept, NPS returns `Framed-IP-Address = 255.255.255.255` (a network policy attribute, RFC 2865). This value should not appear in accounting, but if it does the Proxy must treat it as a Placeholder IP.
- WiNG's `Acct-Session-Id` changes with every AP. On a Roam, a Stop and a Start with different session IDs arrive for the same user and IP.
- RADIUS goes through the Controller, so NPS sees a single RADIUS client: the VX9000 (`10.21.253.230`).

## Evidence from the capture of 2026-09-24

A 9-minute capture of the accounting NPS forwards (125 Accounting-Requests, 37 users) settled several open questions:

| Question | Answer |
|---|---|
| Does a real `Framed-IP-Address` arrive? | Yes, in **all 125** packets, Starts included: the prerequisite below is met |
| Interim interval | 300 s exactly |
| Username forms | 58 bare, 47 UPN, 20 NT4 |
| Shared Accounts | 5 users on several IPs, up to 4 at once; no IP shared by two users |
| Roams | 15 user+IP pairs with more than one `Acct-Session-Id` |
| Machine Accounts | none in this window, so the Username Filter is untested against live data |
| Username convention | `name.surname` in all three forms: 11 of 11 NT4 names, 11 of 12 UPN prefixes and 12 of 13 bare names contain a dot. The domain rules can therefore build the Canonical Username without a directory lookup (NFR-06). |
| Spurious Stops | Of 34 Stops, **22 are followed by a Start or Interim-Update for the same user and IP** (median 3 s later, at most 161 s); only 12 end a session. With Logout-on-Stop on, roughly two Stops in three would remove a Mapping while the client is still connected. |

## Blocking prerequisite

The Proxy sees **only accounting** (Packet-Type 4). It never receives the Access-Requests for roaming and re-authentication, even though those carry IP and identity in the NPS logs.

Before any development, confirm that the accounting reaching NPS carries a real `Framed-IP-Address`, at least in Interim-Updates. The live capture measured above is that check; if the Controller does not send it, the Controller must be configured to include the client IP in its accounting.

## NPS configuration

1. Create a Remote RADIUS Server Group with the Proxy as its only member: the Proxy's IP, accounting port 18131, and a dedicated shared secret. On the Proxy, NPS is then configured as a RADIUS Client with that secret.
2. In the connection request policy `Secure Wireless Connections`, Accounting section: forward accounting requests to that group.
3. Check that local file logging stays enabled, so the Vector pipeline keeps working for the comparison.

## Timeout vs interim interval

With Logout-on-Stop off, only Interim-Updates keep a Mapping alive. In the capture of 2026-09-24 the Controller sends them every **5 minutes** (300 s exactly, measured over 21 intervals), which also limits how long a Roam's spurious Stop can leave a Mapping wrong.

The interval is declared to the Proxy as `UserId.InterimIntervalMinutes` (default 5) and the Timeout must cover at least two intervals, so that one lost update doesn't expire a live Mapping. The default `UserId.TimeoutMinutes` is **15**. The Proxy refuses to start if the Timeout is less than twice the interval. If the interval changes on the Controller, change both.

## Firewall configuration

- Create a dedicated API account whose Admin Role is limited to XML API > User-ID Agent.
- List the **management IPs of both HA peers** as Firewalls in the Proxy's configuration, in failover order, not the cluster VIP. During a failover the VIP belongs to neither peer, and moving it can take minutes in some deployments, so anything sent in that window is lost. With both management IPs, an attempt against the failed peer times out and the Proxy moves straight to the other one (answer 1 below).
- Consider a shorter `Firewalls:TimeoutSeconds` than the default 10: the firewall team suggests 3–5 s per attempt. It's a site choice. It bounds how long a Batch waits on a dead peer, and it has to stay above the API's normal response time.

## Answers from the firewall team (2026-10-05)

1. **How does the passive HA peer answer a User-ID call?** The firewall team says it **accepts the call, records
   the Mapping and syncs it to the active peer over HA1**. It doesn't refuse the call. The team didn't say
   whether this was tested on the cluster or taken from the documentation.
   - **What it means for the Proxy.** Any peer that answers is a valid target. That covers the case where the
     Proxy keeps writing to a peer that has since become passive, after a failover back with preemption. The
     current rule (fail over only when a peer can't be reached, start next time from the last peer that
     answered) is enough. This closes the failover question in [specs.md](specs.md#rejected-and-closed).
   - **What stays a risk.** If HA1 is late or down, a Mapping written on one peer may not reach the other.
     That's inherent to HA and holds for the VIP too: the Proxy can't see it.
   - **Retries are harmless.** A peer can apply a Batch and fail to answer. The Proxy then sends the same
     Batch to the other peer, and the HA sync brings the same Batch back to the first one. The result is the
     same set of Mappings, because a Batch carries no state ([ADR 0006](adr/0006-no-mapping-state-in-the-proxy.md)).
2. **Does a `<logout>` entry with `name=A`, `ip=X` remove a Mapping `X → B`?** **No.** PAN-OS matches the name and
   the IP. It finds no Mapping and answers `Delete mapping failed`, which the Proxy already ignores
   (`UidMessage.ParseResponse`). The sources are the PAN-OS documentation of `<logout>` and a community case
   where a Logout with a different name failed in exactly this way.
   - **What it means for the Proxy.** A late Stop from A, arriving after B took the IP, can't remove B's
     Mapping.
   - **What it doesn't change.** Logout-on-Stop stays **off by default**. The main risk was never across
     users. It's the spurious Stops measured above: 22 of 34 Stops are for the same user and IP that stay
     connected, and there the names match. Before turning it on for Shared Accounts, see
     [issue 11](issues/11-how-wide-a-logout-without-blockstart-reaches.md).
