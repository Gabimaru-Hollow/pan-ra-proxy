# PAN RADIUS Accounting Proxy

Turns RADIUS accounting into User-ID Mappings on a Palo Alto firewall, so that traffic from a client IP can be attributed to the user who authenticated on it.

## Language

### RADIUS side

**RADIUS Client**:
A trusted sender of accounting packets to the Proxy, identified by its source IP and holding a shared secret with it.
_Avoid_: RADIUS server, radius-server, NAS (when the sender is a Forwarding Server)

**Proxy**:
This service: it receives accounting from RADIUS Clients and sends Logins and Logouts to the firewall. It never forwards RADIUS packets and never takes part in authentication.
_Avoid_: RADIUS proxy, radius-client, accounting server

**Placeholder IP**:
A Framed IP value (`0.0.0.0`, `255.255.255.255`, `255.255.255.254`) that stands for "no real address yet", typically because the client has authenticated but has not yet obtained a DHCP lease. Never becomes a Mapping.
_Avoid_: invalid IP, null IP, fake IP

### User-ID side

**Mapping**:
The association of one username with one IP address, as held by the firewall's User-ID table. An IP belongs to at most one Mapping (the latest Login for that IP wins), but one username may hold several Mappings at once.
_Avoid_: Entry, user-id update, binding

**Login**:
An instruction to the firewall to create or refresh a Mapping, carrying a **Timeout**. Triggered by an Accounting-Start or Interim-Update that carries a real IP (not a Placeholder IP), never by authentication itself.
_Avoid_: Entry, login entry

**Logout**:
An instruction to the firewall to remove a Mapping before its Timeout expires.
_Avoid_: Entry, logout entry, delete

**Logout-on-Stop**:
The policy of turning an Accounting-Stop into a Logout. Off by default: a Mapping then ends only by Timeout or by being overwritten by a later Login.
_Avoid_: stop handling, auto-logout

**Timeout**:
How long the firewall keeps a Mapping without receiving another Login for it. An attribute of a Login, not a concept of its own.
_Avoid_: TTL, expiry, lifetime

**Batch**:
One call to the Firewall carrying every Login and Logout gathered within a fixed window that starts at the first accounting packet. Within a Batch, the latest change for each IP wins: a Login replaces every earlier change for its IP, and a Logout is dropped when another user's Login already holds that IP. So the result doesn't depend on the order of the entries.
_Avoid_: uid-message, message, payload

**Firewall**:
A Palo Alto firewall that receives Logins and Logouts directly: the one segmenting the WiFi VLAN or its HA peer, tried in configured order.
_Avoid_: endpoint, PAN API, pan-api-endpoint, Panorama

**Canonical Username**:
The single username form every Mapping uses: the NT4 name (`DOMAIN\user`), whatever form the client authenticated with. Clients send three forms: NT4, UPN and bare (no domain at all).
_Avoid_: normalized name, SAM name, down-level name

**Shared Account**:
One username in use on several devices at the same time, each with its own IP. It produces one Mapping per IP, and a Logout for one of them leaves the others alone.
_Avoid_: multi-session user, concurrent login

**Username Rewrite**:
A pattern-based substitution that turns a received username into the Canonical Username (e.g. `user@domain.local` into `DOMAIN\user`).
_Avoid_: username mapping, normalization rule

**Name Translation**:
A directory lookup that converts a UPN into its NT4 name, for users whose UPN prefix differs from their account name, where the domain rules can't produce the Canonical Username. Off by default.
_Avoid_: name resolution, lookup, TranslateName

**Username Filter**:
A pattern matched against the username exactly as received, before any Username Rewrite, that decides which usernames never become Mappings; a matching accounting packet is dropped.
_Avoid_: exclusion list, blacklist

**Machine Account**:
A computer's own directory identity (`HOST$`, `host/fqdn`) authenticating without a user. Always dropped by the Username Filter; never becomes a Mapping.
_Avoid_: computer login, device account

### Deployment

**Controller**:
The wireless controller (Extreme WiNG VX9000) that relays all AP RADIUS traffic through itself, so upstream servers see it as the only NAS.
_Avoid_: WLC, AP (when meaning the controller)

**Forwarding Server**:
The RADIUS server (NPS) that authenticates clients and forwards their accounting to the Proxy; in this deployment it is the Proxy's only RADIUS Client.
_Avoid_: NPS proxy, upstream RADIUS

**Roam**:
A client moving between APs while keeping its username and IP but getting a new accounting session, which shows up as a Stop from the old AP and a Start from the new one, in no guaranteed order.
_Avoid_: reconnect, handover
