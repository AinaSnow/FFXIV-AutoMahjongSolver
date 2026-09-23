import assert from "node:assert/strict";
import { auditDebugCapture } from "./audit-debug-packets.mjs";
const version = "2026.09.15.0000.0000";
const start = {e:"capture-start",schema_version:1,capture:"raw-zone-receive",protocol_inference:false,
  environment:{game_version:version,client_variant:"Emj",plugin_build_id:"test"}};
const packet = {e:"raw-packet",t:"2026-09-23T10:00:00Z",sequence:1,opcode:"0xBEEF",segment_length:34,payload_length:2,payload_hex:"CAFE"};
const end = {e:"capture-end",packets:1,dropped:0,rejected:0,stream_complete:true,reason:"left-table"};
const audit = records => auditDebugCapture(records.map(r=>JSON.stringify(r)).join("\n"),version);
const good = audit([start,packet,end]);
assert.deepEqual(good.blockers, []);
assert.equal(good.verified, false);
assert.equal(good.openingBoundaryVerified, false);
assert.deepEqual(good.inventory, [{opcode:"0xBEEF",count:1,payloadLengths:[2]}]);
assert.ok(!JSON.stringify(good).includes("CAFE"));
assert.ok(audit([start,packet]).blockers.includes("missing_footer_capture_interrupted"));
assert.ok(audit([start,{...packet,payload_hex:"GG"},end]).blockers.includes("malformed_records"));
assert.ok(audit([start,{...packet,segment_length:99},end]).blockers.includes("malformed_records"));
assert.ok(audit([start,{...packet,sequence:2},end]).blockers.includes("sequence_gap_or_duplicate"));
assert.ok(audit([start,packet,{...end,dropped:1}]).blockers.includes("queue_or_size_limit_loss"));
assert.ok(audit([start,packet,{...end,rejected:1}]).blockers.includes("transport_read_rejected"));
assert.ok(audit([start,packet,{...end,packets:2}]).blockers.includes("footer_packet_count_mismatch"));
assert.ok(audit([start,packet,{...end,reason:"size-limit"}]).blockers.includes("size_limit_reached"));
assert.ok(audit([start,packet,end,packet]).blockers.includes("records_after_footer"));
assert.ok(audit([start,start,packet,end]).blockers.includes("misplaced_or_duplicate_header"));
assert.ok(audit([{...start,environment:{}},packet,end]).blockers.includes("game_version_mismatch_or_unknown"));
assert.ok(audit([start,packet,{...packet,sequence:2,t:"2026-09-23T09:00:00Z"},{...end,packets:2}]).blockers.includes("timestamps_out_of_order"));
assert.ok(auditDebugCapture(JSON.stringify(start)+"\n{",version).blockers.includes("malformed_records"));
assert.ok(audit([start,{...end,packets:0}]).blockers.includes("no_packets"));
assert.throws(()=>auditDebugCapture("","bad-version"));
console.log("debug packet audit tests passed");

const diagnostic = {e:"capture-diagnostic",t:packet.t,reason:"invalid-segment-length",failed_checks:["invalid-segment-length"],
  layout_verified:false,header_offset_from_ipc:-16,requested_header_bytes:32,header_hex:"AB".repeat(32)};
const diagnosticEnd = {...end,packets:0,rejected:100,stream_complete:false,rejection_counts:{"invalid-segment-length":100},
  diagnostic_samples:2,diagnostic_dropped:0,diagnostic_unsampled:98};
const diagnosticStart = {...start,schema_version:2};
const diagnosticResult = audit([diagnosticStart,diagnostic,diagnostic,diagnosticEnd]);
assert.deepEqual(diagnosticResult.blockers,["no_packets","transport_read_rejected","incomplete_capture"]);
assert.equal(diagnosticResult.packets,0);
assert.equal(diagnosticResult.diagnostics.samples,2);
assert.deepEqual(diagnosticResult.inventory,[]);
assert.ok(!JSON.stringify(diagnosticResult).includes(diagnostic.header_hex));
assert.ok(audit([diagnosticStart,{...diagnostic,header_hex:"AA"},diagnosticEnd]).blockers.includes("malformed_records"));
assert.ok(audit([diagnosticStart,diagnostic,diagnostic,diagnostic,diagnosticEnd]).blockers.includes("diagnostic_sample_limit_exceeded"));
assert.ok(audit([diagnosticStart,diagnostic,diagnostic,{...diagnosticEnd,rejection_counts:{bad:100}}]).blockers.includes("invalid_rejection_counts"));
assert.ok(audit([diagnosticStart,diagnostic,diagnostic,{...diagnosticEnd,diagnostic_samples:0}]).blockers.includes("diagnostic_count_mismatch"));
const mixed = audit([diagnosticStart,diagnostic,packet,{...diagnosticEnd,packets:1,rejected:1,
  diagnostic_samples:1,diagnostic_unsampled:0,rejection_counts:{"invalid-segment-length":1}}]);
assert.deepEqual(mixed.inventory,good.inventory);
assert.deepEqual(mixed.blockers,["transport_read_rejected","incomplete_capture"]);
console.log("bounded header diagnostic audit tests passed");
