# 6. The Name Translation caches never shrink

**Severity:** Low · **Status:** Verified; needs a decision

**What.** `CachingNameTranslator` overwrites an expired entry when the same UPN comes back, but never removes entries whose UPN doesn't come back. The same holds for the unknown-domain list in `WindowsNameTranslator`. Growth is bounded by the distinct UPNs seen since the Proxy started, at about 100 bytes each: 100,000 distinct UPNs is about 10 MB. It only applies when a `Lookup` rule is configured.

**To decide.** Accept it, since the service restarts on upgrades, or prune expired entries when the cache passes a size.

[← All known issues](README.md)
