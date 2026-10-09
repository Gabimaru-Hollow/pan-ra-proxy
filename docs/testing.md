# Testing

Two levels: unit and module tests in `tests/PanRaProxy.Tests` (`dotnet test`), and an end-to-end replay of a live capture against the real Proxy.

`dotnet test` also leaves the uid-messages its in-process end-to-end test produced in
`artifacts/tests/*.uid-messages.log`, one section per Batch with the Logins and Logouts and the raw
XML, so a run can be read afterwards instead of only asserted on.

## Test-driven development

Code changes go test-first. This is how the project works, not a preference.

1. **Red.** Write the test that states the behaviour, and watch it fail for the reason you expect. A test that has never failed has not shown it can.
2. **Green.** Write the smallest change that passes it.
3. **Refactor** with the tests green.
4. **Gate.** Before proposing a commit, `.\build\test-all.ps1 -Quiet` is green, replay included (see below). Run `-SkipReplay` only for a change that cannot reach the Proxy's behaviour, and say so.

A bug is fixed the same way: first a test that reproduces it (the defects under *Resolved* in [issues/README.md](issues/README.md) were each reproduced before they were fixed), then the fix. A doubtful case that cannot be reproduced yet is not fixed: it is recorded in [issues/](issues/README.md) and the code stays as it is.

**Where a test goes.** The test tree mirrors the source tree: `Radius/`, `Mappings/`, `Firewall/`, `Options/`, `Diagnostics/` and `Startup/` in `tests/PanRaProxy.Tests` match the folders in `src/PanRaProxy`, and `EndToEndTests.cs` composes the whole graph the Proxy ships (`AddPanRaProxy`), replacing only what varies. Test a module through its interface, with the terms of [GLOSSARY.md](../GLOSSARY.md): `CanonicalUsernameTests` asks what a username becomes, `ProxyStartupTests` goes from arguments to an exit code. If a test has to reach into a module to ask its question, the interface is the thing to change.

**What the unit and module tests cover** (the list grows with the Proxy, so a new requirement from [specs.md](specs.md) adds its row here):
- request and response authenticators, against packets generated independently from RFC 2866 (`tests/fixtures/generate_radius_fixtures.py`, not from the Proxy's own code);
- the attribute reader against malformed and random input: it must never throw on packet content;
- accounting into Login and Logout: Placeholder IPs, Machine Accounts, Logout-on-Stop, the three username forms, Shared Accounts;
- the Batch window and the queue limits, on a `FakeTimeProvider`, not on the wall clock;
- XML API responses (success, global error, per-entry failures), through a stub `HttpMessageHandler`;
- TLS trust, options validation, and the startup surface (switches, exit codes, `--check-config`).

**What stays out of reach of a test, and where it is recorded.** Anything that needs an elevated run, the service account or a real Firewall is not tested; it is listed in [issues/](issues/README.md) with how to verify it by hand (for example [issue 10](issues/10-what-set-secret-does-to-the-machine.md)). Tests replace those effects with fakes on purpose, because a test session must not change ACLs or services.

**Two independent implementations.** The replay below computes its expected Mappings in Python from the FreeRADIUS detail file, so a mistake in the C# rules cannot quietly become the expectation. Do not "fix" the script by copying the C# logic into it.

## Both at once

`build\test-all.ps1` runs the unit tests, publishes the executable and replays the newest capture in the
repository root against it, then prints a summary. It exits 0 when every step that ran passed.

```powershell
.\build\test-all.ps1 -Secret testing123          # or set $env:PANRA_REPLAY_SECRET once
.\build\test-all.ps1 -Quiet                      # replay RESULT lines only; transcript in artifacts\e2e\replay-console.log
.\build\test-all.ps1 -SkipReplay                 # unit tests only
.\build\test-all.ps1 -Speed 1 -ReplayArgs '--logout-on-stop'
```

Without a capture or a secret, the replay is skipped with a warning rather than failed, since captures
never leave the machine they were taken on. `-Pcap` and `-Detail` pick a capture other than the newest,
and `-DomainArgs` replaces the default domain rules (the ones in the example below). A wrong secret
fails the replay slowly, because it waits out the acknowledgement timeout for every packet.

The replay hands its secrets to the Proxy as `PANRAPROXY_RADIUS_SECRET` and `PANRAPROXY_FIREWALL_API_KEY`. A secret
file in `%ProgramData%\PanRaProxy\secrets` wins over those variables (ADR 0007). On a machine where
`--set-secret` has been run, the Proxy would therefore use the installed RADIUS secret instead of the
capture's, and every packet would be discarded. Replay on a development machine.

## In CI

`.github/workflows/ci.yml` runs on every push to `master` and every pull request:
- **Unit and module tests:** `build\test-all.ps1 -SkipReplay` on Windows. The replay stays local, since captures are never committed.
- **Links between documents:** [lychee](https://github.com/lycheeverse/lychee) in offline mode checks every relative link in the Markdown files and its `#anchor`. Write a reference to another document as a link with its anchor, not as prose ("environment.md, answer 2"), so a renamed heading fails the build instead of going stale. To run the same check locally: `lychee --offline --include-fragments '**/*.md'`.

## End-to-end replay of a live capture

`tools/e2e/live-replay.py` runs the whole path on one machine, with no installation and no administrator rights:

1. starts a **mock PAN-OS User-ID endpoint** over HTTPS with a throwaway certificate, which the Proxy verifies through `Firewalls:CaFile` (TLS is really exercised, not disabled);
2. starts **PanRaProxy** as a console process, with settings on the command line and secrets in environment variables;
3. **replays the Accounting-Requests from a pcap** at their original timing (`--speed` compresses it), checking that each one is acknowledged;
4. **prints every Login and Logout live**, Batch by Batch, as the Firewall would see them;
5. **compares the Mappings received** with those expected from the FreeRADIUS `detail` file of the same capture.

The expectation is computed by the script itself, from the detail file, with rules written in Python. It is a second implementation of the same rules, so a mistake in the C# code doesn't quietly become the expected result. (It already caught one difference, which turned out to be a bug in the script: FreeRADIUS escapes backslashes in the detail file.)

```powershell
dotnet publish src\PanRaProxy\PanRaProxy.csproj -c Release -p:PublishSingleFile=true -o artifacts\publish

python tools\e2e\live-replay.py `
    --pcap nps-acct-20260924-12.pcap --detail detail-20260924-13 `
    --secret testing123 --speed 30 `
    --nt4-domain XDOMAIN --upn-suffix xdomain.local=XDOMAIN --upn-suffix example.com=XDOMAIN
```

Exit code 0 means the Mappings match the capture. Useful options:

| Option | Effect |
|---|---|
| `--speed 1` | Original timing: the 9-minute capture takes 9 minutes, and Batches form as they would in production |
| `--logout-on-stop` | Turns Logout-on-Stop on, in the Proxy and in the expectation |
| `--reject <text>` | The mock rejects entries whose username contains that text, to exercise per-entry failures (events 3007/3008) |
| `--nt4-domain`, `--upn-suffix` | Turned into `UserId:Domain:Rules` for the Proxy, and applied by the script to compute the expectation |
| `--batch-window-ms` | Changes the Batch window, to see its effect on the number of calls |
| `--radius-port`, `--https-port` | When the defaults are taken |

Output, all under `artifacts/` and none of it in the repository:

| File | Content |
|---|---|
| `artifacts/e2e/proxy.log` | The Proxy's own log, at Debug level, so dropped packets show their reason |
| `artifacts/e2e/mock-requests.jsonl` | One line per call the mock received: time, whether the `X-PAN-KEY` header was there, the entries, and the raw `uid-message` |
| `artifacts/e2e/mappings.csv` | Every Mapping sent, with how many times it was sent and whether the detail file expected it |
| `artifacts/e2e/run.log` | The console transcript |

### Captures

**Captures are not kept in the repository** (`.gitignore`): they contain usernames, MAC addresses and IPs, and a pcap allows an offline attack on the RADIUS shared secret. Keep them where your other production evidence lives, and use a lab secret for anything shared.

To make one on the Proxy's host: capture UDP on the accounting port. The `detail` file is what FreeRADIUS writes when it receives the same traffic; any decoder that prints one record per packet would do, as long as the script can parse it.
