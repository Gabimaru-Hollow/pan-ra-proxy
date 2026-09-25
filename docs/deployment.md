# Deployment: User-ID on the WiFi VLAN via NPS accounting

How the Proxy fits the target environment: 802.1X clients on an Extreme WiNG wireless network, authenticated by NPS, with a Palo Alto Firewall segmenting the WiFi VLAN. Terms are defined in [CONTEXT.md](../CONTEXT.md).

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

## Blocking prerequisite

The Proxy sees **only accounting** (Packet-Type 4). It never receives the Access-Requests for roaming and re-authentication, even though those carry IP and identity in the NPS logs.

Before any development, confirm that the accounting reaching NPS carries a real `Framed-IP-Address`, at least in Interim-Updates. The check and the WiNG configuration it needs are described in `CONTEXT-nps-vector-userid.md` §7.3 and §8. That document is external and not part of this repository.

## NPS configuration

1. Create a Remote RADIUS Server Group with the Proxy as its only member: the Proxy's IP, accounting port 18131, and a dedicated shared secret. On the Proxy, NPS is then configured as a RADIUS Client with that secret.
2. In the connection request policy `Secure Wireless Connections`, Accounting section: forward accounting requests to that group.
3. Check that local file logging stays enabled, so the Vector pipeline keeps working for the comparison.

## Timeout vs interim interval

With Logout-on-Stop off, only Interim-Updates keep a Mapping alive. In the capture of 2026-09-24 the Controller sends them every **5 minutes** (300 s exactly, measured over 21 intervals), which also limits how long a Roam's spurious Stop can leave a Mapping wrong.

The interval is declared to the Proxy as `UserId.InterimIntervalMinutes` (default 5) and the Timeout must cover at least two intervals, so that one lost update doesn't expire a live Mapping. The default `UserId.TimeoutMinutes` is **15**. The Proxy refuses to start if the Timeout is less than twice the interval. If the interval changes on the Controller, change both.

## Firewall configuration

- Create a dedicated API account whose Admin Role is limited to XML API > User-ID Agent.
- List the active firewall and its HA peer as Firewalls in the Proxy's configuration, in failover order.
