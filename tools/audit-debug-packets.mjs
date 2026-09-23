// Audit transport evidence only. Unknown opcodes never acquire inferred message names.
import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";

export function auditDebugCapture(text, expectedVersion) {
  const versionPattern = /^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$/;
  if (!versionPattern.test(expectedVersion)) throw new Error("Expected game version YYYY.MM.DD.NNNN.NNNN");
  const blockers = new Set(), inventory = new Map();
  let header = null, footer = null, packets = 0, malformed = 0, first = null, last = null;
  for (const line of text.replace(/^\uFEFF/, "").split(/\r?\n/).filter(line => line.trim())) {
    let record;
    try { record = JSON.parse(line); }
    catch { malformed++; continue; }
    if (!record || typeof record !== "object") { malformed++; continue; }
    if (footer) blockers.add("records_after_footer");
    if (record.e === "capture-start") {
      if (header || packets) blockers.add("misplaced_or_duplicate_header");
      header ??= record;
      if (record.schema_version !== 1 || record.capture !== "raw-zone-receive" || record.protocol_inference !== false)
        blockers.add("unsupported_capture_schema");
    } else if (record.e === "capture-end") {
      if (footer) blockers.add("duplicate_footer");
      footer ??= record;
    } else if (record.e === "raw-packet") {
      packets++;
      if (!header) blockers.add("packet_before_header");
      if (record.sequence !== packets) blockers.add("sequence_gap_or_duplicate");
      const time = Date.parse(record.t);
      if (!Number.isFinite(time)) blockers.add("invalid_timestamp");
      else {
        if (last !== null && time < last) blockers.add("timestamps_out_of_order");
        first ??= time; last = time;
      }
      const valid = /^0x[0-9a-f]{4}$/i.test(record.opcode) &&
        Number.isInteger(record.payload_length) && record.payload_length >= 0 && record.payload_length <= 65504 &&
        record.segment_length === record.payload_length + 32 && typeof record.payload_hex === "string" &&
        record.payload_hex.length === record.payload_length * 2 && /^[0-9a-f]*$/i.test(record.payload_hex);
      if (!valid) { malformed++; continue; }
      const opcode = "0x" + record.opcode.slice(2).toUpperCase();
      let entry = inventory.get(opcode);
      if (!entry) inventory.set(opcode, entry = { opcode, count: 0, payloadLengths: new Set() });
      entry.count++; entry.payloadLengths.add(record.payload_length);
    } else blockers.add("unknown_record_type");
  }
  if (!header) blockers.add("missing_header");
  const observedVersion = header?.environment?.game_version ?? null;
  if (observedVersion !== expectedVersion) blockers.add("game_version_mismatch_or_unknown");
  if (!packets) blockers.add("no_packets");
  if (malformed) blockers.add("malformed_records");
  if (!footer) blockers.add("missing_footer_capture_interrupted");
  else {
    if (footer.packets !== packets) blockers.add("footer_packet_count_mismatch");
    if (![footer.dropped, footer.rejected].every(n => Number.isInteger(n) && n >= 0)) blockers.add("invalid_footer_counts");
    if (footer.dropped !== 0) blockers.add("queue_or_size_limit_loss");
    if (footer.rejected !== 0) blockers.add("transport_read_rejected");
    if (footer.stream_complete !== true) blockers.add("incomplete_capture");
    if (!["disabled", "left-table", "unload", "size-limit"].includes(footer.reason)) blockers.add("unknown_end_reason");
    if (footer.reason === "size-limit") blockers.add("size_limit_reached");
  }
  if (first !== null && new Date(first).toISOString().slice(0,10) < expectedVersion.slice(0,10).replaceAll(".", "-"))
    blockers.add("capture_predates_requested_build");
  return { schemaVersion: 1, captureFormat: "raw-zone-receive", expectedVersion, observedVersion,
    clientVariant: header?.environment?.client_variant ?? null, pluginBuildId: header?.environment?.plugin_build_id ?? null,
    verified: false, openingBoundaryVerified: false, sourceSha256: createHash("sha256").update(text).digest("hex"),
    packets, malformed, first: first === null ? null : new Date(first).toISOString(), last: last === null ? null : new Date(last).toISOString(),
    dropped: footer?.dropped ?? null, rejected: footer?.rejected ?? null, endReason: footer?.reason ?? null,
    inventory: [...inventory.values()].sort((a,b) => a.opcode.localeCompare(b.opcode))
      .map(entry => ({ ...entry, payloadLengths: [...entry.payloadLengths].sort((a,b) => a-b) })),
    blockers: [...blockers], remainingValidation: ["identify Mahjong opcodes using synchronized UI evidence",
      "validate field layouts, red tiles, kan/dora, honba/kyotaku and server confirmations",
      "verify opening boundary and complete multi-hand replay; file completeness is not protocol completeness"] };
}

async function main(args) {
  const [input, version, output] = args;
  if (args.length !== 3) throw new Error("Usage: node tools/audit-debug-packets.mjs capture.ndjson 2026.09.15.0000.0000 report.json");
  const report = auditDebugCapture(await readFile(input, "utf8"), version);
  await writeFile(output, JSON.stringify(report,null,2) + "\n", {flag:"wx"});
  console.log(JSON.stringify({report:output, packets:report.packets, verified:false, blockers:report.blockers}));
}
if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url)
  main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
