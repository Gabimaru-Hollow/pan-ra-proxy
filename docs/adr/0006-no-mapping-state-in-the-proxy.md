# The Proxy keeps no state of the Mappings

The Proxy doesn't remember which Mappings it has sent. Every Start and Interim-Update becomes a Login again, and the Firewall's User-ID table stays the only record of who holds which IP. The only state that lives beyond one Batch is the cache of directory answers (Name Translation), which is a lookup cache, not a record of Mappings.

## Considered Options

- **A table of sent Mappings (IP → username, last sent), used to skip Logins the Firewall already holds.** Ruled out:
  - **The refresh is the mechanism.** A Mapping lasts its Timeout (15 min) because each Interim-Update (every 5 min) sends the Login again. Skipping known ones would mean tracking every Timeout and resending before it runs out: a copy of the Firewall's table, maintained in the Proxy.
  - **A second record of the same knowledge.** It diverges without a sound when the Firewall reboots, commits or fails over, or when a Batch is lost (P3-4). Today a lost Batch costs nothing, because the next Interim-Update rebuilds it. With the table, that Mapping would stay known to the Proxy and missing on the Firewall.
  - **Little to save.** The replay of the live capture sends 87 entries for 40 distinct Mappings in 9 minutes: about 5 redundant entries a minute, grouped into single HTTP calls.
- **A cache from raw username to Canonical Username for every rule.** The rules are regular expressions that cost microseconds. Only the directory lookup is worth caching, and it already is.

## Consequences

- Correctness can't rest on remembering what was sent. Within a Batch it rests on keying changes by IP (CONTEXT.md, Batch). Across Batches it rests on the Interim-Updates, which bound any wrong Mapping to one interval.
- Out-of-order accounting (RADIUS doesn't guarantee order, and NPS retransmits) is repaired the same way, within one Interim-Update interval. A Logout sent across Batches after the IP changed hands can't remove the new holder's Mapping, because PAN-OS matches a Logout on name and IP (deployment.md, answer 2).
