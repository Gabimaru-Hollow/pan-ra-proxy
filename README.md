# PanRaProxy

A Windows service that receives RADIUS accounting and turns it into Palo Alto Networks User-ID Mappings. Each Start and Interim-Update becomes a Login, and a Stop can become a Logout. They are grouped into Batches and sent to the Firewall's XML API.

It is a .NET 8 rework of [lithnet/pan-ra-proxy](https://github.com/lithnet/pan-ra-proxy). The parts of the upstream that already worked were carried over and the rest was rewritten ([ADR 0002](docs/adr/0002-dotnet8-refactor-over-go.md)). This repository no longer holds the upstream code ([ADR 0008](docs/adr/0008-the-repository-holds-only-the-dotnet8-proxy.md)).

## Requirements

- Windows Server with the .NET 8 runtime bundled: the executable is self-contained.
- PAN-OS 10 or later, with an API account limited to XML API > User-ID Agent.
- A RADIUS accounting source that sends a real `Framed-IP-Address` (see [environment.md](docs/environment.md)).

## Documentation

| | |
|---|---|
| [GLOSSARY.md](GLOSSARY.md) | The glossary: Mapping, Login, Logout, Batch, Canonical Username… |
| [INSTALL.md](INSTALL.md) | MSI, secrets, logs, console runs, `--check-config`, `--dry-run` |
| [docs/environment.md](docs/environment.md) | The target environment and what the live capture measured |
| [docs/testing.md](docs/testing.md) | Unit tests and the end-to-end replay, through `build\test-all.ps1` |
| [docs/refactoring-spec.md](docs/refactoring-spec.md) | Requirements, and the review of the upstream code |
| [docs/adr/](docs/adr/) | Decisions |
| [docs/issues/](docs/issues/README.md) | Known issues, unverified or awaiting a decision |

## Build

```powershell
.\build\test-all.ps1 -SkipReplay          # unit tests only
.\build\test-all.ps1 -Secret <secret>     # plus publish and the replay of a local live capture
.\build\build-msi.ps1 -Version 1.0.1      # self-contained executable and MSI
```

Live captures hold real usernames and are never committed: see [testing.md](docs/testing.md).

## License

MIT, see [LICENSE.txt](LICENSE.txt). The upstream copyright is kept, as the license requires.
