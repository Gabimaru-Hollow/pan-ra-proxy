# Two fixed secrets, set by the binary and encrypted at rest

**Status:** Accepted and implemented, 2026-09-30. Supersedes [ADR 0003](0003-secrets-as-protected-files-provided-after-install.md). What only an elevated run on a real install can confirm is in [issues.md](../issues.md), item 10.

The Proxy has exactly two secrets, with fixed names:
- `radius`: the RADIUS shared secret, the same for every RADIUS Client;
- `firewall-api-key`: the Firewall API key.

The site settings name no secret at all: `Radius:Clients` is a list of hosts, and `Firewalls` has no `ApiKeySecretName`.

The administrator sets each secret with the Proxy's own executable, from an elevated prompt:

```
PanRaProxy.exe --set-secret radius
PanRaProxy.exe --set-secret firewall-api-key
```

The command reads the value without echo, or from standard input for automation. It encrypts the value with DPAPI in `LocalMachine` scope and writes it as `dpapi:v1:<base64>` to `%ProgramData%\PanRaProxy\secrets\<name>`. It sets that folder's ACL: protected (not inherited from ProgramData), read for `NT SERVICE\PanRaProxy`, full control for SYSTEM and Administrators. If the service is running, the command restarts it, since the Proxy reads its secrets only at startup. An unknown name exits 2.

The service only reads: it never writes its secrets or its configuration. For development and tests, `PANRAPROXY_RADIUS_SECRET` and `PANRAPROXY_FIREWALL_API_KEY` are used when no secret file exists.

## Threat model

An administrator or SYSTEM account that is compromised can read the secrets whatever is done. That is outside this decision. Against everyone else:

| Exposure | ADR 0003 (plaintext file + ACL) | This decision |
|---|---|---|
| A local user without rights | ACL | ACL, the same. A `LocalMachine` blob can be decrypted by any process on the machine, so the ACL is still what protects it there |
| A copy of the file taken off the machine (file-level backup, support bundle, a commit by mistake) | Plaintext | Ciphertext: the machine key doesn't travel with the file |
| A snapshot of the whole VM, or a disk image | Plaintext | Recoverable: the DPAPI machine key can be extracted offline from that image's SYSTEM and SECURITY hives |
| The Proxy's own process compromised | Reads the secrets | Reads the secrets, and still can't change its configuration |
| Plaintext left on disk while provisioning | None: the script prompts | None: the command prompts |

What this decision adds is protection of copies taken off the machine, plus one tool fewer. ADR 0003 dismissed DPAPI because on the machine it adds little over the ACL. That remains true, and the table says so. What changed is that copies of files do leave the machine, and the provisioning tool can now be the binary itself.

## Considered Options

- **One secret per RADIUS Client**, as before (`Radius:Clients:N:SecretName`). The RFC recommends it, and it limits what one leaked secret allows. Here the RADIUS Clients are the Forwarding Servers: one NPS, two at most, run by the same team, and the capture that could leak one secret would leak the other. Rejected as complexity without a matching gain. **Reopen when** a RADIUS Client run by someone else joins, for example a NAS sending directly. Then per-client secrets come back as an option to `--set-secret`.
- **Secrets inline in the site settings, encrypted on first start by the service** (the first draft of this ADR). Rejected for two reasons:
  - **The service would need write access to its own configuration**, and to the folder for an atomic replace. A compromised process could then redirect `Firewalls:Endpoints`, or turn off certificate validation, and send the API key elsewhere.
  - **The value would sit in plaintext** in a file readable by local users (`%ProgramData%\PanRaProxy` inherits `Users: Read`) until the next start, and in editor backups and shadow copies afterwards.

  Rewriting a JSON file that allows comments without damaging it is its own problem too.
- **Keep `Set-PanRaProxySecret.ps1`**: it works, but it's a second tool to ship, sign and keep in step with the binary, and it goes against the binary-first approach (architecture review, candidate 12).
- **`--set-secret` and `--set-api-key` as two commands**: the same code path twice. One verb with a fixed set of names is one help line, and a third secret costs a name, not a command.
- **User-scoped DPAPI, Credential Manager, registry, service environment variables**: covered by ADR 0003 and still rejected for its reasons.

## Consequences

- **The service accepts a plaintext value but reports it.** An old ADR 0003 file without the `dpapi:v1:` prefix still works, with a Warning at startup and in `--check-config`. `--set-secret` replaces it. Environment variables are for development and aren't reported.
- **A blob that won't decrypt stops the Proxy with event 3106**, which says to run `--set-secret` again. That happens after the machine is reinstalled, or when the file is copied from another host.
- **The configuration changes** for existing site settings. `Radius:Clients:N:SecretName` and `Firewalls:ApiKeySecretName` go away. `Radius:Clients:N` keeps `Host` only, or becomes a list of host strings. Validation reports the old keys as obsolete instead of silently ignoring them.
- **Code to change:** `RadiusClientRegistry` keeps a set of addresses and one secret, and "one address with two different secrets" disappears. `Set-PanRaProxySecret.ps1` is removed, and `INSTALL.md`, `testing.md` and `tools/e2e/live-replay.py` change with it.
- **A console run still creates nothing.** Only `--set-secret`, run by the administrator on purpose, writes to the machine. It's the first command that does, and the natural companion of `--install` if candidate 12 ever happens.
