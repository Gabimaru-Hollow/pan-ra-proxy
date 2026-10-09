<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->

## Start here

This is a fork of `lithnet/pan-ra-proxy`. It turns RADIUS accounting into Palo Alto User-ID Mappings.
Read these before changing anything, in this order:

1. **`GLOSSARY.md`** — the glossary. Use these words (Mapping, Login, Logout, Batch, Canonical Username, Placeholder IP, Shared Account, RADIUS Client, Firewall) in code, tests, logs and commits.
2. **`docs/adr/`** — decisions already taken. Don't re-litigate them; amend the ADR if one turns out wrong.
3. **`docs/refactoring-spec.md`** — requirements (FR/NFR), the code review of the upstream and the configuration. Event IDs are listed in `src/PanRaProxy/Diagnostics/Log.cs`, metrics in `Diagnostics/ProxyMetrics.cs`.
4. **`docs/environment.md`** — the environment and the evidence measured from the live capture.
5. **`docs/architecture-review.md`** — open candidates for deepening the .NET 8 code. Candidates, not decisions.

Also: `INSTALL.md` (MSI, secrets, logs, console runs), `docs/testing.md` (`dotnet test` plus the end-to-end replay of a live capture, both through `build/test-all.ps1`) and `docs/issues/` (one file per known issue that is unverified or needs a decision, with an index in `README.md`).

Two things to know before running anything:

- `src/PanRaProxy` is the .NET 8 service, and the only code in the repository. The upstream `lithnet/pan-ra-proxy` is its origin, not a codebase kept alongside it (ADR 0008). Its code survives only in the history, at the fork baseline `049992e`, where the spec's file and line references point (`git show 049992e:<path>`).
- Live captures (`detail-*`, `*.pcap`) hold real usernames and IPs and allow an offline attack on the RADIUS shared secret. They are git-ignored, and anything derived from them that gets committed must be anonymised.

## Working with the user

- Reply in Italian. Docs, code and commit messages stay in English.
- The user decides each step: propose with an honest assessment of the trade-offs, ask, then act. Record a doubtful or unverified case as a new file in `docs/issues/` (and its row in the index), an ADR or the architecture review, and leave the code as it is.
- Commit only when asked. End each piece of work by proposing the commit message, then write it to a file in the scratchpad and run `git commit -F <file>`.
- Code changes go test-first: a red test, then the fix. Before proposing a commit, `build\test-all.ps1 -Quiet` is green, replay included (`docs/testing.md`).
