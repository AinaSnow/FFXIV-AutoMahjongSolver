# tools/re/

Ad-hoc reverse-engineering scripts for inspecting local addon memory dumps (`memdumps-*.ndjson.gz`) captured when **Memory snapshots** (`MemoryDumpRecorder`) are enabled in plugin settings.

| Script | Purpose |
|---|---|
| `analyze-memdumps.mjs` | Aggregate summary statistics over a local memdump directory |
| `peek-memdump.mjs` | Print record shapes and keys from a memdump file |
| `diff-memdump-bytes.mjs` | Analyze byte-position variability across addon/root blobs |
| `check-seat-offsets-local.mjs` | Validate candidate `OurSeat` offsets on local snapshots |
| `find_seat_offsets.py` | Compare memdumps across hands to locate seat/wind/dealer offsets |
| `find-discard-offset.mjs` | Scan addon byte transitions to locate per-seat discard arrays |
| `find-discard-offset-agent.mjs` | Scan `AgentEmj` buffer transitions for per-seat discard arrays |
| `inspect-discard-transition.mjs` | Dump all changed addon bytes across a single +1 discard transition |
| `scan-agent-offsets.mjs` / `2` / `3` | Scan `AgentEmj` (`agent_b64`) for rotating seat/dealer and riichi flags |
| `scan-dealer-seat-candidates.mjs` | Scan `addon_b64` for byte/int32 fields rotating across hand boundaries |
| `scan-dealer-int32.mjs` | Int32 sibling to `scan-dealer-seat-candidates.mjs` |
| `scan-wind-id-candidates.mjs` | Scan `addon_b64` for wind tile-ID (`27..30`) candidates |
