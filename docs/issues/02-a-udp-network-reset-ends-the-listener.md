# 2. A UDP "network reset" ends the listener

**Severity:** Low · **Status:** Unverified

**What.** On Windows a UDP socket reports two kinds of ICMP errors on the next receive:
- *port unreachable*, as `ConnectionReset`. The listener turns it off (`SIO_UDP_CONNRESET`) and also ignores it.
- *TTL expired*, as `NetworkReset` (`SIO_UDP_NETRESET`). It is neither turned off nor caught, so it reaches the outer catch, and the listener exits the process (event 3103). Windows then restarts the service 60 s later, and the accounting of that minute is lost.

**To verify.** Only a routing problem between the Proxy and the Forwarding Server produces TTL-expired messages for the Accounting-Responses. It is rare on a LAN.

**Possible fix.** Treat `SocketError.NetworkReset` like `ConnectionReset`, and turn it off with the matching IOControl.

[← All known issues](README.md)
