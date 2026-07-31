// Run: node tools/test-parse-mahjong-packets.mjs

import assert from "node:assert/strict";
import {
  MahjongPacketDecoder,
  convertEventsToMjai,
  decodeMahjongLog,
  decodePhysicalTile,
  formatEventText,
  formatTile,
  parseArguments,
  parsePacketLine,
  summarizeEvents,
} from "./parse-mahjong-packets.mjs";

function line(messageId, opcode, payload, timestamp = "2026-07-31 10:00:00.000") {
  return `${timestamp}|Ipc|RECV|${opcode}|DOWN_ID_${messageId}_TEST|${payload.length}|0x1|0x1|${payload.toString("hex").toUpperCase()}`;
}

function matchStart() {
  const payload = Buffer.alloc(48);
  payload.writeInt32LE(3, 24);
  for (const offset of [28, 32, 36, 40]) payload.writeInt32LE(250, offset);
  return payload;
}

function handStart() {
  const payload = Buffer.alloc(104);
  payload.writeInt32LE(1, 8);
  payload.writeInt32LE(3, 24);
  payload.writeUInt32LE(0x00010116, 28);
  [237, 302, 237, 224].forEach((score, i) => payload.writeInt32LE(score, 32 + i * 4));
  [8, 18, 22, 9, 10, 31, 15, 24, 0, 6, 6, 19, 30]
    .forEach((tile, i) => payload.writeInt32LE(tile, 48 + i * 4));
  return payload;
}

function redHandStart() {
  const payload = handStart();
  [34, 35, 36].forEach((tile, i) => payload.writeInt32LE(tile, 48 + i * 4));
  return payload;
}

function draw(seat, physical) {
  const payload = Buffer.alloc(24, 0xff);
  payload.writeInt32LE(seat, 0);
  payload.writeUInt32LE(0x100, 4);
  payload.writeUInt16LE(physical, 8);
  payload.writeInt32LE(1, 20);
  return payload;
}

function discard(seat, physical, action = 0x110) {
  const payload = Buffer.alloc(32, 0xff);
  payload.writeInt32LE(seat, 0);
  payload.writeUInt32LE(1, 4);
  payload.writeUInt32LE(action, 8);
  payload.writeUInt16LE(physical, 12);
  payload.writeInt32LE(1, 24);
  return payload;
}

function call(seat, action, first, second) {
  const payload = Buffer.alloc(24, 0xff);
  payload.writeInt32LE(seat, 0);
  payload.writeUInt32LE(action, 4);
  payload.writeUInt16LE(first, 12);
  payload.writeUInt16LE(second, 14);
  payload.writeInt32LE(0, 20);
  return payload;
}

assert.equal(formatTile(0), "1m");
assert.equal(formatTile(22), "5s");
assert.equal(formatTile(27), "E");
assert.equal(formatTile(33), "C");
assert.equal(formatTile(34), "?");
assert.equal(parseArguments(["session.log"]).limit, Number.POSITIVE_INFINITY);
assert.equal(parseArguments(["session.log", "--format", "summary"]).format, "summary");

assert.deepEqual(decodePhysicalTile(89), {
  physical: 89,
  id: 22,
  tile: "5s",
  copy: 1,
  red: true,
});
assert.deepEqual(decodePhysicalTile(140), {
  physical: 140,
  id: 13,
  tile: "5p",
  copy: 0,
  red: true,
});
assert.equal(decodePhysicalTile(0xffff), null);

const parsed = parsePacketLine(line(637, "0x018E", handStart()));
assert.equal(parsed.messageId, 637);
assert.equal(parsed.payload.length, 104);
assert.equal(parsePacketLine("not a packet"), null);

const redEvents = decodeMahjongLog(line(637, "0x018E", redHandStart()));
assert.deepEqual(redEvents[0].initialHand.slice(0, 3), [
  { id: 4, tile: "5m", red: true },
  { id: 13, tile: "5p", red: true },
  { id: 22, tile: "5s", red: true },
]);
assert.deepEqual(convertEventsToMjai(redEvents)[1].tehais[0].slice(0, 3), ["5mr", "5pr", "5sr"]);

const lines = [
  line(636, "0x0129", matchStart()),
  line(637, "0x018E", handStart(), "2026-07-31 10:00:01.000"),
  line(638, "0x00B0", draw(0, 0), "2026-07-31 10:00:02.000"),
  line(638, "0x00B0", draw(3, 89), "2026-07-31 10:00:03.000"),
  line(641, "0x0156", discard(0, 68), "2026-07-31 10:00:04.000"),
  line(638, "0x00B0", call(1, 0x600, 60, 64), "2026-07-31 10:00:05.000"),
  line(641, "0x0156", discard(3, 89, 0x111), "2026-07-31 10:00:06.000"),
  line(639, "0x0273", Buffer.alloc(256), "2026-07-31 10:00:07.000"),
].join("\n");

const events = decodeMahjongLog(lines);
assert.equal(events.length, 8);
assert.deepEqual(events[0].scores, [25000, 25000, 25000, 25000]);
assert.equal(events[1].selfSeatName, "N");
assert.equal(events[1].doraIndicator.tile, "5s");
assert.equal(events[1].doraIndicatorRaw, 0x00010116);
assert.equal(events[1].doraFlagsRaw, 0x101);
assert.deepEqual(events[1].scores, [23700, 30200, 23700, 22400]);
assert.equal(events[1].initialHand.length, 13);
assert.equal(events[2].tile, null, "opponent draw must remain concealed");
assert.equal(events[2].relativeSeat, 1);
assert.equal(events[3].tile.tile, "5s");
assert.equal(events[3].tile.red, true);
assert.equal(events[3].relativeSeat, 0);
assert.equal(events[5].kind, "chi");
assert.equal(events[5].fromSeatName, "E");
assert.equal(events[5].fromRelativeSeat, 1);
assert.equal(events[5].calledTile.tile, "9p");
assert.deepEqual(events[5].consumed.map((tile) => tile.tile), ["7p", "8p"]);
assert.equal(events[6].riichi, true);
assert.match(formatEventText(events[6]), /N DISCARD 5s#1 red \(riichi\)/);
assert.equal(events[7].type, "hand_end");
assert.deepEqual(summarizeEvents(events), {
  events: 8,
  hands: 1,
  counts: { call: 1, discard: 2, draw: 2, hand_end: 1, hand_start: 1, match_start: 1 },
  calls: { chi: 1, pon: 0 },
  discards: { riichi: 1, tsumogiri: 0, afterCall: 0 },
});

const mjai = convertEventsToMjai(events);
assert.deepEqual(mjai[0], { type: "start_game" });
assert.equal(mjai[1].type, "start_kyoku");
assert.equal(mjai[1].bakaze, "E");
assert.equal(mjai[1].kyoku, 2);
assert.equal(mjai[1].oya, 1);
assert.deepEqual(mjai[1].scores, [22400, 23700, 30200, 23700]);
assert.equal(mjai[1].tehais[0].length, 13);
assert.deepEqual(mjai[1].tehais[1], Array(13).fill("?"));
assert.deepEqual(mjai[2], { type: "tsumo", actor: 1, pai: "?" });
assert.deepEqual(mjai[3], { type: "tsumo", actor: 0, pai: "5sr" });
assert.deepEqual(mjai[4], { type: "dahai", actor: 1, pai: "9p", tsumogiri: false });
assert.deepEqual(mjai[5], {
  type: "chi",
  actor: 2,
  target: 1,
  pai: "9p",
  consumed: ["7p", "8p"],
});
assert.deepEqual(mjai.slice(6), [
  { type: "reach", actor: 0 },
  { type: "dahai", actor: 0, pai: "5sr", tsumogiri: false },
  { type: "reach_accepted", actor: 0 },
  { type: "end_kyoku" },
  { type: "end_game" },
]);

const decoder = new MahjongPacketDecoder({ includeUnknown: true });
assert.equal(decoder.processLine(line(640, "0x039C", Buffer.alloc(504)))[0].type, "hand_end");
assert.deepEqual(decoder.processLine(line(642, "0x01EB", Buffer.alloc(576))), [], "roster packets must stay private");

console.log("All Mahjong packet parser assertions passed.");
