# Testing

Two levels: unit and module tests in `tests/PanRaProxy.Tests` (`dotnet test`), and an end-to-end replay of a live capture against the real Proxy.

`dotnet test` also leaves the uid-messages its in-process end-to-end test produced in
`artifacts/tests/*.uid-messages.log`, one section per Batch with the Logins and Logouts and the raw
XML, so a run can be read afterwards instead of only asserted on.

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
