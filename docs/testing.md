# Testing

Two levels: unit and module tests in `tests/PanRaProxy.Tests` (`dotnet test`), and an end-to-end replay of a live capture against the real Proxy.

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
| `--batch-window-ms` | Changes the Batch window, to see its effect on the number of calls |
| `--radius-port`, `--https-port` | When the defaults are taken |

Output: the console shows the live Batches and the summary; `artifacts/e2e/proxy.log` holds the Proxy's own log (run at Debug level, so dropped packets show their reason).

### Captures

**Captures are not kept in the repository** (`.gitignore`): they contain usernames, MAC addresses and IPs, and a pcap allows an offline attack on the RADIUS shared secret. Keep them where your other production evidence lives, and use a lab secret for anything shared.

To make one on the Proxy's host: capture UDP on the accounting port. The `detail` file is what FreeRADIUS writes when it receives the same traffic; any decoder that prints one record per packet would do, as long as the script can parse it.
