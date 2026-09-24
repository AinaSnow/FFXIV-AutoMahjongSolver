import assert from "node:assert/strict";
import { auditCapture } from "./audit-mahjong-capture.mjs";
function line(id,size,day="2026-09-23",action=0x100) {
 const payload=Buffer.alloc(size); if(size>=12) payload.writeUInt32LE(action,id===641?8:4);
 return `${day} 10:00:00.000|Ipc|RECV|0x${id.toString(16)}|DOWN_ID_${id}_TEST|${size}|0x1|0x2|${payload.toString("hex")}`;
}
const version="2026.09.15.0000.0000";
const old=auditCapture(line(637,104,"2026-07-31"),version);
assert(old.blockers.includes("capture_predates_requested_build"));
const normal=auditCapture([line(636,48),line(637,104),line(638,24),line(639,256),line(641,32,"2026-09-23",0x110)].join("\n"),version);
assert.equal(normal.mahjong,5);assert.equal(normal.verified,false);assert.deepEqual(normal.blockers,[]);
const unknown=auditCapture([line(638,24,"2026-09-23",0x700),line(641,31),line(642,40)+"private-roster",line(637,104).replace("|104|","|105|")].join("\n"),version);
assert.equal(unknown.rosterExcluded,1);assert.equal(unknown.malformed,1);
assert(unknown.blockers.includes("layout_length_changed:641"));
assert(unknown.blockers.includes("unhandled_actions:638"));
assert(!JSON.stringify(unknown).includes("private-roster"));
const backwards=auditCapture([line(637,104,"2026-09-24"),line(638,24)].join("\n"),version);
assert.equal(backwards.outOfOrder,1);assert(backwards.blockers.includes("timestamps_out_of_order"));
assert.equal(auditCapture(line(641,32,"2026-09-23",0xa10),version).packets[0].unknownActions.length,0);
const conflict=auditCapture([line(636,48),line(637,104).replace("0x27d","0x27c")].join("\n"),version);
assert(conflict.blockers.some(b=>b.startsWith("opcode_name_conflict:")));
assert.throws(()=>auditCapture("", "wrong"));
assert.equal(auditCapture(line(641,32,"2026-09-24",0x113),version).packets[0].unknownActions.length,0);
console.log("capture audit: all checks passed");
