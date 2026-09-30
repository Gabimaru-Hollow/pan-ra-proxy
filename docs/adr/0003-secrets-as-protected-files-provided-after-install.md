# Secrets are protected files provided after installation

**Status:** Superseded by [ADR 0007](0007-two-fixed-secrets-set-by-the-binary.md) (2026-09-30): the binary sets two fixed secrets, encrypted with DPAPI, in this same protected folder.

The RADIUS shared secrets and the Firewall API key never pass through the MSI, the site settings file or a command line. After installation, `Set-PanRaProxySecret.ps1` writes each secret as a file in `%ProgramData%\PanRaProxy\secrets`, a folder whose ACL is protected (not inherited from ProgramData) and grants read only to `NT SERVICE\PanRaProxy`, plus full control to SYSTEM and Administrators. The Proxy reads the file named in `SecretName` / `ApiKeySecretName`, falling back to an environment variable of the same name for development and tests.

## Considered Options

- **Service environment variables** (`HKLM\SYSTEM\CurrentControlSet\Services\PanRaProxy\Environment`): no code needed, but that key is readable by local users by default.
- **MSI properties**: end up in verbose MSI logs and in the setup tool's command line.
- **Windows Credential Manager**: per user profile, awkward to provision for a virtual account from a setup tool.
- **DPAPI-encrypted file**: with machine scope, any process on the machine can decrypt it, so it adds little over the ACL; with user scope it needs the virtual account's profile at provisioning time.

## Consequences

Running as the virtual account `NT SERVICE\PanRaProxy`, instead of upstream's `NETWORK SERVICE`, is what makes the ACL meaningful: only this service's SID can read the folder.
