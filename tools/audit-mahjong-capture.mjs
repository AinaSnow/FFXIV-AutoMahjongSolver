// Inventory evidence without trusting old opcode maps or enabling a live protocol.
import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import { parsePacketLine } from "./parse-mahjong-packets.mjs";

const EXPECTED = new Map([[636,48],[637,104],[638,24],[639,256],[640,504],[641,32]]);
const DRAW_ACTIONS = new Set([0x100,0x500,0x600]);
const DISCARD_ACTIONS = new Set([0x110,0x111,0x112,0x113,0xa10]);

export function auditCapture(text, gameVersion, region = "international") {
  if (!/^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$/.test(gameVersion)) throw new Error("Expected game version YYYY.MM.DD.NNNN.NNNN");
  const packets = new Map();
  const problems = new Set();
  let incoming = 0, unnamedIncoming = 0, malformed = 0, rosterExcluded = 0, mahjong = 0;
  let first = null, last = null, previous = null, outOfOrder = 0;
  for (const line of text.replace(/^\uFEFF/, "").split(/\r?\n/)) {
    const columns = line.split("|");
    if (columns[1] !== "Ipc" || columns[2] !== "RECV") continue;
    incoming++;
    const id = /^DOWN_ID_(\d+)_/.exec(columns[4] ?? "");
    if (!id) { unnamedIncoming++; continue; }
    const messageId = Number(id[1]);
    if (messageId === 642) { rosterExcluded++; continue; }
    if (!EXPECTED.has(messageId)) continue;
    const packet = parsePacketLine(line);
    if (!packet) { malformed++; continue; }
    mahjong++;
    const timestamp = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+$/.test(packet.timestamp) ? packet.timestamp : null;
    if (!timestamp) problems.add("unrecognized_timestamp");
    else {
      first = first === null || timestamp < first ? timestamp : first;
      last = last === null || timestamp > last ? timestamp : last;
      if (previous !== null && timestamp < previous) outOfOrder++;
      previous = timestamp;
    }
    let entry = packets.get(messageId);
    if (!entry) packets.set(messageId, entry = { messageId, count:0, opcodes:new Set(), payloadLengths:new Set(), actions:new Set(), unknownActions:new Set(), ambiguousFiveCount:0 });
    entry.count++; entry.opcodes.add(packet.opcode); entry.payloadLengths.add(packet.payload.length);
    if (packet.payload.length !== EXPECTED.get(messageId)) problems.add(`layout_length_changed:${messageId}`);
    const actionOffset = messageId === 638 ? 4 : messageId === 641 ? 8 : null;
    if (actionOffset !== null && packet.payload.length >= actionOffset + 4) {
      const action = packet.payload.readUInt32LE(actionOffset);
      const formatted = `0x${action.toString(16).toUpperCase()}`;
      entry.actions.add(formatted);
      if (!(messageId === 638 ? DRAW_ACTIONS : DISCARD_ACTIONS).has(action)) entry.unknownActions.add(formatted);
      const tileOffset = messageId === 641 ? 12 : action === 0x100 ? 8 : null;
      if (tileOffset !== null && packet.payload.length >= tileOffset + 2) {
        const physical = packet.payload.readUInt16LE(tileOffset);
        const kind = physical >>> 2, copy = physical & 3;
        if (kind === 4 || kind === 13 || (kind === 22 && copy > 1)) entry.ambiguousFiveCount++;
      }
    }
  }
  const buildDate = gameVersion.slice(0,10).replaceAll(".","-");
  if (!mahjong) problems.add("no_named_mahjong_packets");
  if (first && first.slice(0,10) < buildDate) problems.add("capture_predates_requested_build");
  if (malformed) problems.add("malformed_mahjong_packets");
  if (outOfOrder) problems.add("timestamps_out_of_order");
  for (const id of [636,637,638,641]) if (!packets.has(id)) problems.add(`missing_message:${id}`);
  if (!packets.has(639) && !packets.has(640)) problems.add("missing_hand_result");
  const opcodeOwners = new Map();
  for (const entry of packets.values()) for (const opcode of entry.opcodes) {
    const normalized = opcode.toUpperCase();
    if (opcodeOwners.has(normalized) && opcodeOwners.get(normalized) !== entry.messageId) problems.add(`opcode_name_conflict:${normalized}`);
    opcodeOwners.set(normalized,entry.messageId);
  }
  const inventory = [...packets.values()].sort((a,b)=>a.messageId-b.messageId).map(entry => {
    if (entry.opcodes.size !== 1) problems.add(`multiple_opcodes:${entry.messageId}`);
    if (entry.unknownActions.size) problems.add(`unhandled_actions:${entry.messageId}`);
    return {...entry, opcodes:[...entry.opcodes].sort(),payloadLengths:[...entry.payloadLengths].sort((a,b)=>a-b),
      actions:[...entry.actions].sort(),unknownActions:[...entry.unknownActions].sort()};
  });
  return {schemaVersion:1, declaredGameVersion:gameVersion, declaredRegion:region,
    versionEvidence:"operator supplied; export timestamps cannot prove client build", verified:false,
    sourceSha256:createHash("sha256").update(text).digest("hex"),
    incoming, unnamedIncoming, mahjong, malformed, rosterExcluded, first, last, outOfOrder,
    packets:inventory, blockers:[...problems],
    remainingValidation:["confirm installed game version and Packet Logger name table version",
      "confirm each opcode and payload field against synchronized UI observations",
      "honba and kyotaku offsets", "opponent kan and added dora events", "self-kan server confirmation deduplication",
      "red five identities for all three suits", "complete multi-hand replay; observed timestamps do not prove no packet loss"]};
}

async function main(args) {
  const [input,gameVersion,output] = args;
  if (!input || !gameVersion || !output || args.length !== 3) throw new Error("Usage: node tools/audit-mahjong-capture.mjs session.log 2026.09.15.0000.0000 report.json");
  const report=auditCapture(await readFile(input,"utf8"),gameVersion);
  // Never overwrite an existing evidence report or install a protocol profile.
  await writeFile(output,JSON.stringify(report,null,2)+"\n",{flag:"wx"});
  console.log(JSON.stringify({report:output, mahjong:report.mahjong, verified:false, blockers:report.blockers}));
}
if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) main(process.argv.slice(2)).catch(error=>{console.error(error.message);process.exitCode=1;});
