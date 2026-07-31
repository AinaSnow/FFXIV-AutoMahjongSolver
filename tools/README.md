# tools/

Reverse-engineering scripts used to map FFXIV's Mahjong addon memory layout.
Not part of the runtime path — these run offline against captured hex dumps
to find offsets, AtkValue indices, and node IDs.

## Scripts

| Script | Purpose | Input |
|---|---|---|
| `analyze_snaps.py` | Walk a captured snapshot and surface candidate offsets | `emj-snapshot-*.txt` written by `/mjauto findtiles` |
| `scan_tiles.py` | Find tile-id encoding in a snapshot — pins the texture base | `emj-snapshot-*.txt` |
| `diff_nodes.py` | Diff two `/mjauto walknodes` captures to spot visibility / id changes between states | Two `emj-walknodes-*.txt` files |
| `gen_icon.ps1` | Generate the plugin's `Images/Icon.png` from a source SVG | `Images/Icon.svg` |
| `sync-corpus.ps1` | Mirror the R2 telemetry corpus to local `corpus/`, gunzipping as it arrives | `wrangler` + Cloudflare credentials |
| `extract-fixture.mjs` | Convert one memdump record into a Track 0 replay fixture | A memdumps `.ndjson(.gz)` + a `seq` number |
| `test-extract-fixture.mjs` | Smoke test the above against a synthetic memdump | None |
| `parse-mahjong-packets.mjs` | Decode confirmed Mahjong packets into text, NDJSON, or a summary | A Packet Logger session `.log` |
| `test-parse-mahjong-packets.mjs` | Regression-test the confirmed packet layouts with synthetic payloads | None |
| `mortal-bridge.mjs` | Replay a Packet Logger session, or follow a file written by an append-capable producer, through Mortal | Packet log + bot config JSON |
| `test-mortal-bridge.mjs` | Exercise Mortal subprocess JSONL transport with a fake bot | None |

## Workflow for a new variant

1. Sit at a Mahjong table on the unknown client.
2. `/mjauto findtiles` — captures hand tiles + memory window to
   `emj-findtiles-*.txt`.
3. `/mjauto walknodes` — captures the addon's node tree to
   `emj-walknodes-*.txt`.
4. `python tools/scan_tiles.py emj-findtiles-*.txt` — pins the variant's
   tile texture base.
5. `python tools/diff_nodes.py <state1> <state2>` — spots node IDs that
   differ between game states (call prompts, etc.).
6. Produce a new `data/layouts/<variant>.json` with the discovered values.
7. Plugin auto-discovers it on next launch.

See [`data/layouts/README.md`](../data/layouts/README.md) for the JSON
schema.

### Parsing a Packet Logger session

Human matches send a dedicated Mahjong message family that is not present in
NPC matches. Decode the confirmed messages without exposing roster packets:

```powershell
node tools/parse-mahjong-packets.mjs "C:\path\to\Session.log" --format text
node tools/parse-mahjong-packets.mjs "C:\path\to\Session.log" --format ndjson
node tools/parse-mahjong-packets.mjs "C:\path\to\Session.log" --format summary
node tools/parse-mahjong-packets.mjs "C:\path\to\Session.log" --format mjai
```

The decoder currently covers match/hand initialization, draws, chi, pon, and
discards including tsumogiri, riichi, and post-call markers. Message IDs 639
and 640 are unconfirmed and only appear as metadata with `--include-unknown`.
Message 642 is always suppressed because its payload contains player names.
Numeric opcodes are patch-specific; the decoder keys on Packet Logger's
`DOWN_ID_*` names instead. The `mjai` format rotates wind seats so the local
player is always actor 0, hides opponent starting hands/draws with `?`, and
emits the lifecycle and reach sequence expected by Mortal. Launch Mortal with
player ID `0` when consuming this stream.

Replay a completed session through Mortal:

```powershell
node tools/mortal-bridge.mjs "C:\path\to\Session.log" --bot-config mortal-bridge.json
```

Follow a file that is actively appended by its producer:

```powershell
node tools/mortal-bridge.mjs "C:\path\to\Session.log" --bot-config mortal-bridge.json --follow
```

In follow mode, existing events rebuild Mortal's state with `can_act=false`.
Only newly appended events can produce actions on stdout. Status and Mortal
stderr go to stderr, keeping stdout valid action-only JSONL. The FFXIV Network
Packet Analysis Tool's session files are manual exports and do not grow after
export, so `--follow` is not a live source for that tool. It remains useful for
testing another append-capable packet producer. Copy
`mortal-bridge.example.json` outside version control and adjust the executable,
distribution, and Mortal working directory.

### Live Mortal integration

The Dalamud plugin captures the six confirmed Mahjong receive opcodes directly
in-process and never captures message 642, whose payload contains player names.
It converts the payloads to MJAI and owns the Mortal WSL subprocess, so no
separate Python backend needs to be started manually.

Configure these values under **Settings > Mortal AI** while Mortal is disabled:

- WSL distribution, for example `Ubuntu-25.10`
- Mortal directory inside WSL, for example `/mnt/e/path/to/Mortal/mortal`
- Python executable, normally `python`

Enable Mortal before a match or before the next hand begins. The current opcode
map is patch-specific and comes from the verified human-match capture; the
offline parser continues to key on the analysis tool's stable `DOWN_ID_*` names.

## Pulling user telemetry

Once the Cloudflare Worker is deployed (see `server/README.md`):

```powershell
# Pull everything new
.\tools\sync-corpus.ps1

# Just findings from a specific date — fast iteration loop
.\tools\sync-corpus.ps1 -Stream findings -Date 2026-05-07

# A single install's full memdump history
.\tools\sync-corpus.ps1 -Stream memdumps -InstallId 8e4c0a12-...
```

Output lands in `./corpus/{stream}/{install_id}/{date}/` — both the `.gz`
and the gunzipped NDJSON sit side by side. The script is incremental: a
local file existing means "already synced", so re-runs only fetch what's
new. Pass `-Force` to redownload everything if a sync was interrupted
mid-decompress.

### Extracting a replay fixture from a memdump

Once you have a local memdumps NDJSON and the `seq` of the frame you want to lock down:

```powershell
node tools/extract-fixture.mjs corpus/memdumps/<install>/<date>/memdumps-*.ndjson 1234 --name state15_chi_pon_simultaneous
```

The fixture lands in `tests/Mahjong.Plugin.Dalamud.Tests/Replay/fixtures/`. The
tool decodes `atk_b64` into typed slots (Int/UInt/Bool); strings get null since
telemetry captures the pointer only, not the bytes it dereferences to. Open the
generated file, fill in `expected.*` fields, commit. The
`ReplayFixtureTests.Fixture_matches_expected_snapshot` theory picks it up
automatically on next test run.

### Telemetry workflow

After syncing, run Claude Code from the corpus directory for analysis:

```powershell
cd corpus
claude
```

Then ask things like *"group every `variant_miss` finding by addon_name +
game_version and tell me which client builds have no matching variant"*
— Claude has direct file access to the whole corpus.

## Status

Scripts are ad-hoc one-offs — no test coverage, no formal API, run on
demand. The `/mjauto` capture commands they consume live in the plugin's
`Commands/MjAutoCommand.cs`.
