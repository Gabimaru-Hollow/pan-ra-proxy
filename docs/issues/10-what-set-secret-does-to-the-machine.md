# 10. What `--set-secret` does to the machine

**Severity:** Medium · **Status:** Unverified: needs an elevated run on an install

**What.** The tests cover the command's logic (reading, encryption, the stored format, exit codes). They replace its three effects on the machine with fakes, because a test session isn't elevated and must not change ACLs or services:

- **The ACL** (`SecretCommand.ProtectDirectory`):
  - inheritance from ProgramData removed;
  - full control for SYSTEM and Administrators;
  - read for `NT SERVICE\PanRaProxy`, resolved by name (if the service isn't installed, the name doesn't resolve and a note says so).
- **The service restart** (`SecretCommand.RestartServiceIfRunning`): stop, then start, with 30 s for each.
- **The prompt** (`SecretCommand.ReadFromConsole`): no echo, the value typed twice, or piped from standard input.

**To verify.** On an installed machine, from an elevated PowerShell:
1. Run `--set-secret radius` twice: once typed, once piped.
2. Check the folder's ACL (`Get-Acl $env:ProgramData\PanRaProxy\secrets | Format-List`).
3. Check that a non-elevated prompt gets exit 1 with the "elevated prompt" message.
4. Check that the running service restarts and logs 4004 again.
5. Check that `--check-config` from a non-elevated prompt reports 2003 rather than "not set".

[← All known issues](README.md)
