# 11. How wide a Logout without `blockstart` reaches

**Severity:** Low (only with Logout-on-Stop on) · **Status:** Unverified

**What.** The Proxy sends a Logout as `name` + `ip`, with no `blockstart`. The firewall team quoted the PAN-OS documentation for that form: it removes "all the mappings of that user". The wording comes from the terminal-server (port-mapping) context, so it can be read two ways:
- **All of the user's Mappings on that IP.** This is likely, since `blockstart` only exists for multi-user systems. Nothing changes for the Proxy.
- **All of the user's Mappings, on every IP.** A Stop from a Shared Account on one IP would then also remove its Mappings on the other IPs, against FR-11. The capture has 5 such users, with up to 4 IPs at once.

The community case the team cited shows that a different name makes the Logout fail. It doesn't settle this.

**When it matters.** Only with `UserId:LogoutOnStop` on. It's off by default, and stays off because of the spurious Stops ([environment.md](../environment.md)).

**To verify.** Before turning Logout-on-Stop on, on a test Firewall:
1. Log in the same test user on two test IPs.
2. Send a Logout for one IP.
3. Check `show user ip-user-mapping all` for the other IP.

[← All known issues](README.md)
