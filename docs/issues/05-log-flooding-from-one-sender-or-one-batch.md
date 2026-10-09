# 5. Log flooding from one sender or one Batch

**Severity:** Low–Medium · **Status:** Verified; needs a decision

**What.** Some events are written once per occurrence, with no limit:
- **3101**, Warning: once per packet from a sender that isn't a RADIUS Client. By default the Windows Firewall rule accepts any sender (`RADIUS_CLIENTS` empty). A misconfigured NAS, or anyone on the network, can therefore write one Event Log entry per datagram.
- **3007/3008**, Error: once per Login or Logout the Firewall rejects. A persistent rejection repeats at every Interim-Update, for every entry.

The files are bounded (14 × 16 MB). The Event Log overwrites its oldest entries, which are exactly the ones that matter after an incident.

**To decide.** One summary line per sender per interval (for example "1234 packets from X in the last minute"), or accept it and require `RADIUS_CLIENTS` at install time.

[← All known issues](README.md)
