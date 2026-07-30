// Cross-reference real hand boundaries from the games NDJSON against v3+
// AgentEmj agent_b64
// values at those moments. Avoids the noisy "wall-jump in memdump corpus"
// boundaries (which include spurious mid-deal rolls fixed in v0.1.0.10).

import { readdir, readFile } from "node:fs/promises";
import { basename, join } from "node:path";
import { gunzipSync } from "node:zlib";

const memdumpDir = process.argv[2];
const gamesDir = process.argv[3];
const agentIdArg = process.argv.indexOf("--agent-id");
const targetAgentId = agentIdArg >= 0 ? Number(process.argv[agentIdArg + 1]) : 328;
if (!memdumpDir || !gamesDir) {
  console.error("Usage: node tools/scan-agent-offsets3.mjs <memdumpDir> <gamesDir> [--agent-id 328|329|330]");
  process.exit(2);
}

async function readNdjson(path) {
  const raw = await readFile(path);
  const body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  return body.toString("utf8").split("\n").filter(Boolean)
    .map((l) => { try { return JSON.parse(l); } catch { return null; } })
    .filter(Boolean);
}
async function loadAll(d) {
  const all = [];
  try {
    const stack = [d];
    while (stack.length) {
      const cur = stack.pop();
      for (const e of await readdir(cur, { withFileTypes: true })) {
        const p = join(cur, e.name);
        if (e.isDirectory()) stack.push(p);
        else if (e.isFile() && (e.name.endsWith(".gz") || e.name.endsWith(".ndjson")))
          for (const r of await readNdjson(p)) all.push(r);
      }
    }
  } catch {}
  return all;
}

async function listDataFiles(d) {
  const files = [];
  try {
    const stack = [d];
    while (stack.length) {
      const cur = stack.pop();
      for (const e of await readdir(cur, { withFileTypes: true })) {
        const p = join(cur, e.name);
        if (e.isDirectory()) stack.push(p);
        else if (e.isFile() && (e.name.endsWith(".gz") || e.name.endsWith(".ndjson")))
          files.push(p);
      }
    }
  } catch {}
  return files.sort();
}

// Games — retain one hand-start per file, then select the latest contiguous
// handNN run. A day directory can contain several unrelated table sessions,
// while fresh v3 memdumps normally belong to the most recent one.
const gameStarts = [];
for (const path of await listDataFiles(gamesDir)) {
  const records = await readNdjson(path);
  const start = records.find((e) => e.e === "hand-start");
  if (!start) continue;
  const priorEnd = records.find((e) => e.e === "hand-end") ?? null;
  const match = basename(path).match(/-hand(\d+)\./);
  gameStarts.push({
    ts: new Date(start.t).getTime(),
    handNo: match ? Number(match[1]) : -1,
    file: basename(path),
    start,
    priorEnd,
  });
}
gameStarts.sort((a, b) => a.ts - b.ts);
const runs = [];
for (const start of gameStarts) {
  const previous = runs.at(-1)?.at(-1);
  const isRestart = previous
    && ((start.handNo > 0 && previous.handNo > 0 && start.handNo <= previous.handNo)
      || start.ts - previous.ts > 30 * 60 * 1000);
  if (runs.length === 0 || start.handNo === 1 || isRestart)
    runs.push([]);
  runs.at(-1).push(start);
}
const selectedRun = runs.at(-1) ?? [];
const handStarts = selectedRun.map((e) => e.ts);
console.log(`hand-start events: ${gameStarts.length}; selected latest contiguous run: ${handStarts.length}`);
for (const e of selectedRun)
  console.log(
    `  hand=${String(e.handNo).padStart(2)} t=${new Date(e.ts).toISOString()} ` +
    `scores=[${e.start.scores?.join(",") ?? ""}] ` +
    `prior=${e.priorEnd ? `${e.priorEnd.kind}:w${e.priorEnd.winner ?? "-"}:l${e.priorEnd.loser ?? "-"}:d[${e.priorEnd.deltas?.join(",") ?? ""}]` : "-"} ` +
    `file=${e.file}`);
if (handStarts.length < 4) {
  console.log("Need ≥4 hand-starts for rotation analysis. Skipping seat scan.");
}

// Stream files and retain only the first dump in each hand-start window. Local
// corpora can exceed hundreds of MB, so retaining every base64 record is wasteful.
const samplesByStart = new Map();
let agentRecordCount = 0;
for (const path of await listDataFiles(memdumpDir)) {
  for (const r of await readNdjson(path)) {
    const selectedB64 = targetAgentId === r.agent_id
      ? r.agent_b64
      : r.agent_candidates?.find((candidate) => candidate.id === targetAgentId)?.b64;
    if (r.v < 3 || typeof selectedB64 !== "string") continue;
    agentRecordCount++;
    const t = new Date(r.t).getTime();
    for (const ts of handStarts) {
      if (samplesByStart.has(ts) || t < ts + 1000 || t > ts + 30000) continue;
      samplesByStart.set(ts, {
        ts,
        m: {
          ...r,
          agent: Buffer.from(selectedB64, "base64"),
          addon: typeof r.addon_b64 === "string" ? Buffer.from(r.addon_b64, "base64") : null,
          t,
        },
      });
    }
  }
}
console.log(`v>=3 agent ${targetAgentId} memdumps scanned: ${agentRecordCount}`);
const samples = handStarts.map((ts) => samplesByStart.get(ts)).filter(Boolean);
console.log(`memdump samples aligned with hand-start events: ${samples.length}`);

if (samples.length >= 4) {
  const dealerPatterns = [];
  for (let initialDealer = 0; initialDealer < 4; initialDealer++) {
    let dealer = initialDealer;
    const dealers = [dealer];
    for (let i = 1; i < selectedRun.length; i++) {
      const result = selectedRun[i].priorEnd;
      if (result?.kind === "ron" || result?.kind === "tsumo") {
        if (result.winner !== dealer) dealer = (dealer + 1) % 4;
      } else if (result?.kind === "draw") {
        const dealerDelta = result.deltas?.[dealer] ?? 0;
        if (dealerDelta <= 0) dealer = (dealer + 1) % 4;
      }
      dealers.push(dealer);
    }
    dealerPatterns.push({
      initialDealer,
      dealers,
      ourSeats: dealers.map((d) => (4 - d) % 4),
    });
  }
  console.log("\n## Dealer/our-seat patterns implied by hand results:");
  for (const p of dealerPatterns)
    console.log(`  initial=${p.initialDealer} dealer=[${p.dealers.join(",")}] ourSeat=[${p.ourSeats.join(",")}]`);

  function scanBytes(label, selectBuffer) {
    const buffers = samples.map((s) => selectBuffer(s)).filter(Boolean);
    if (buffers.length !== samples.length) return;
    const commonLen = Math.min(...buffers.map((b) => b.length));

    console.log(`\n## Bytes in ${label} with values {0..3}, ≥2 distinct across selected run:`);
    const candidates = [];
    for (let off = 0; off < commonLen; off++) {
      const vals = buffers.map((b) => b[off]);
      if (!vals.every((v) => v >= 0 && v <= 3)) continue;
      const distinct = new Set(vals);
      if (distinct.size < 2) continue;
      candidates.push({ offset: off, distinct: distinct.size, vals });
    }
    candidates.sort((a, b) => b.distinct - a.distinct || a.offset - b.offset);
    console.log(`  ${candidates.length} candidates:`);
    for (const c of candidates.slice(0, 40))
      console.log(`  +0x${c.offset.toString(16).padStart(4, "0")}  distinct=${c.distinct}  vals=[${c.vals.join(",")}]`);

    console.log(`\n## ${label} exact matches for result-derived patterns:`);
    let exactMatches = 0;
    for (let off = 0; off < commonLen; off++) {
      const vals = buffers.map((b) => b[off]);
      for (const p of dealerPatterns) {
        const dealerMatch = vals.every((v, i) => v === p.dealers[i]);
        const seatMatch = vals.every((v, i) => v === p.ourSeats[i]);
        if (!dealerMatch && !seatMatch) continue;
        exactMatches++;
        console.log(
          `  +0x${off.toString(16).padStart(4, "0")} ${dealerMatch ? "dealer" : "ourSeat"} ` +
          `initial=${p.initialDealer} vals=[${vals.join(",")}]`);
      }
    }
    if (exactMatches === 0) console.log("  (none)");

    return { buffers, commonLen };
  }

  const agentScan = scanBytes("agent_b64", (s) => s.m.agent);
  scanBytes("addon_b64", (s) => s.m.addon);

  console.log("\n## Known seat-field candidates:");
  for (const offset of [0x009e, 0x00a6, 0x00ae, 0x0866, 0x0c0d, 0x0cb8]) {
    if (!agentScan || offset >= agentScan.commonLen) continue;
    const vals = samples.map((s) => s.m.agent[offset]);
    console.log(`  +0x${offset.toString(16).padStart(4, "0")}  vals=[${vals.join(",")}]`);
  }

  console.log("\n## Int32 in agent_b64 with values {0..3}, ≥3 distinct across hand-start samples:");
  const intCandidates = [];
  for (let off = 0; agentScan && off < agentScan.commonLen - 4; off += 4) {
    const vals = samples.map((s) => s.m.agent.readInt32LE(off));
    if (!vals.every((v) => v >= 0 && v <= 3)) continue;
    const distinct = new Set(vals);
    if (distinct.size < 3) continue;
    intCandidates.push({ offset: off, distinct: distinct.size, vals });
  }
  intCandidates.sort((a, b) => b.distinct - a.distinct);
  console.log(`  ${intCandidates.length} candidates:`);
  for (const c of intCandidates.slice(0, 30)) {
    console.log(`  +0x${c.offset.toString(16).padStart(4, "0")}  distinct=${c.distinct}  vals=[${c.vals.slice(0, 16).join(",")}]`);
  }
}

console.log("\nDone.");
