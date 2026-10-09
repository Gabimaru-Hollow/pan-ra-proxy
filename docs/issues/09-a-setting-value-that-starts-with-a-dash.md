# 9. A setting value that starts with a dash

**Severity:** Low · **Status:** Verified by reading

**What.** `CommandLine` treats any argument that starts with `-` or `/` and has no `:` as a switch. In the two-token form, a value that starts with a dash is taken as an unknown option, and the Proxy exits with 2. For example, in `--UserId:BatchWindowMs -1` the `-1` is rejected. The `=` form works: `--UserId:BatchWindowMs=-1`.

**To decide.** Document "use `--Key=value`" (the usage already shows only that form), or let a dash argument through when the previous one was a setting without a value.

[← All known issues](README.md)
