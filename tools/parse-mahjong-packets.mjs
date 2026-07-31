// Decode the Mahjong-specific messages emitted by Packet Logger.
//
// Usage:
//   node tools/parse-mahjong-packets.mjs <packet-log> [--format text|ndjson|summary|mjai]
//
// The numeric opcodes can change between game builds. Packet Logger's stable
// DOWN_ID names are used here, and unconfirmed payloads are not interpreted.

import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const SEAT_NAMES = ["E", "S", "W", "N"];
const DRAGON_NAMES = ["P", "F", "C"];
const UNKNOWN_TILE = 0xffff;

export function formatTile(tileId) {
  if (!Number.isInteger(tileId) || tileId < 0 || tileId >= 34) return "?";
  if (tileId < 27) {
    const suit = tileId < 9 ? "m" : tileId < 18 ? "p" : "s";
    return `${(tileId % 9) + 1}${suit}`;
  }
  if (tileId < 31) return SEAT_NAMES[tileId - 27];
  return DRAGON_NAMES[tileId - 31];
}

function decodeTileKind(tileId) {
  if (!Number.isInteger(tileId)) return null;
  if (tileId >= 0 && tileId < 34) return { id: tileId, tile: formatTile(tileId), red: false };
  const redFiveId = tileId === 34 ? 4 : tileId === 35 ? 13 : tileId === 36 ? 22 : null;
  return redFiveId === null ? null : { id: redFiveId, tile: formatTile(redFiveId), red: true };
}

export function decodePhysicalTile(value) {
  if (!Number.isInteger(value) || value === UNKNOWN_TILE) return null;
  const encodedId = value >>> 2;
  const copy = value & 3;

  if (encodedId >= 34) {
    const alias = decodeTileKind(encodedId);
    return alias ? {
      physical: value,
      id: alias.id,
      tile: alias.tile,
      copy,
      red: true,
    } : null;
  }
  const id = encodedId;

  // Only the 5s/copy-1 mapping has been verified against AtkValue red flags.
  // Keep unverified suited-five mappings explicit instead of guessing.
  let red = false;
  if (id === 4 || id === 13) red = null;
  if (id === 22) red = copy === 1 ? true : copy === 0 ? false : null;

  return { physical: value, id, tile: formatTile(id), copy, red };
}

function readUInt16(buffer, offset) {
  return offset + 2 <= buffer.length ? buffer.readUInt16LE(offset) : null;
}

function readInt32(buffer, offset) {
  return offset + 4 <= buffer.length ? buffer.readInt32LE(offset) : null;
}

function readUInt32(buffer, offset) {
  return offset + 4 <= buffer.length ? buffer.readUInt32LE(offset) : null;
}

export function parsePacketLine(line) {
  const columns = line.trimEnd().split("|");
  if (columns.length < 9 || columns[1] !== "Ipc" || columns[2] !== "RECV") return null;

  const idMatch = /^DOWN_ID_(\d+)_/.exec(columns[4]);
  if (!idMatch || !/^[0-9a-f]+$/i.test(columns[8]) || columns[8].length % 2 !== 0) return null;

  const payload = Buffer.from(columns[8], "hex");
  const declaredLength = Number.parseInt(columns[5], 10);
  if (Number.isFinite(declaredLength) && declaredLength !== payload.length) return null;

  return {
    timestamp: columns[0],
    opcode: columns[3],
    name: columns[4],
    messageId: Number.parseInt(idMatch[1], 10),
    payload,
  };
}

function seatFields(seat, selfSeat = null) {
  const validSeat = Number.isInteger(seat) && seat >= 0 && seat < 4;
  const validSelf = Number.isInteger(selfSeat) && selfSeat >= 0 && selfSeat < 4;
  return {
    seat,
    seatName: validSeat ? SEAT_NAMES[seat] : "?",
    relativeSeat: validSeat && validSelf ? (seat - selfSeat + 4) % 4 : null,
  };
}

function packetFields(packet) {
  return {
    timestamp: packet.timestamp,
    messageId: packet.messageId,
    opcode: packet.opcode,
  };
}

export class MahjongPacketDecoder {
  constructor({ includeUnknown = false } = {}) {
    this.includeUnknown = includeUnknown;
    this.selfSeat = null;
    this.lastDiscard = null;
  }

  processLine(line) {
    const packet = parsePacketLine(line);
    return packet ? this.processPacket(packet) : [];
  }

  processPacket(packet) {
    switch (packet.messageId) {
      case 636:
        return this.#decodeMatchStart(packet);
      case 637:
        return this.#decodeHandStart(packet);
      case 638:
        return this.#decodeDrawOrCall(packet);
      case 639:
      case 640:
        return this.#decodeHandEnd(packet);
      case 641:
        return this.#decodeDiscard(packet);
      case 642:
        // This payload contains player names. Deliberately do not expose it.
        return [];
      default:
        return [];
    }
  }

  #decodeMatchStart(packet) {
    if (packet.payload.length < 44) return [];
    const scores = [28, 32, 36, 40].map((offset) => readInt32(packet.payload, offset) * 100);
    this.selfSeat = null;
    this.lastDiscard = null;
    return [{
      ...packetFields(packet),
      type: "match_start",
      modeRaw: readInt32(packet.payload, 24),
      scores,
    }];
  }

  #decodeHandStart(packet) {
    if (packet.payload.length < 100) return [];
    const selfSeat = readInt32(packet.payload, 24);
    const doraIndicatorRaw = readUInt32(packet.payload, 28);
    const doraIndicatorId = doraIndicatorRaw & 0xff;
    const initialHand = [];
    for (let offset = 48; offset < 100; offset += 4) {
      const tile = decodeTileKind(readInt32(packet.payload, offset));
      if (tile) initialHand.push(tile);
    }

    this.selfSeat = selfSeat;
    this.lastDiscard = null;
    return [{
      ...packetFields(packet),
      type: "hand_start",
      handIndex: readInt32(packet.payload, 8),
      selfSeat,
      selfSeatName: seatFields(selfSeat).seatName,
      doraIndicator: decodeTileKind(doraIndicatorId),
      doraIndicatorRaw,
      doraFlagsRaw: doraIndicatorRaw >>> 8,
      scores: [32, 36, 40, 44].map((offset) => readInt32(packet.payload, offset) * 100),
      initialHand,
    }];
  }

  #decodeDrawOrCall(packet) {
    if (packet.payload.length < 24) return [];
    const seat = readInt32(packet.payload, 0);
    const action = readUInt32(packet.payload, 4);
    const common = { ...packetFields(packet), ...seatFields(seat, this.selfSeat) };

    if (action === 0x100) {
      const event = {
        ...common,
        type: "draw",
        tile: seat === this.selfSeat ? decodePhysicalTile(readUInt16(packet.payload, 8)) : null,
        tailFlag: readInt32(packet.payload, 20),
      };
      this.lastDiscard = null;
      return [event];
    }

    if (action === 0x500 || action === 0x600) {
      const previous = this.lastDiscard;
      const event = {
        ...common,
        type: "call",
        kind: action === 0x500 ? "pon" : "chi",
        fromSeat: previous?.seat ?? null,
        fromSeatName: previous?.seatName ?? null,
        fromRelativeSeat: previous?.relativeSeat ?? null,
        calledTile: previous?.tile ?? null,
        consumed: [12, 14]
          .map((offset) => decodePhysicalTile(readUInt16(packet.payload, offset)))
          .filter(Boolean),
        actionRaw: action,
        tailFlag: readInt32(packet.payload, 20),
      };
      this.lastDiscard = null;
      return [event];
    }

    const slots = [];
    for (let offset = 8; offset <= 18; offset += 2) {
      slots.push(readUInt16(packet.payload, offset));
    }
    this.lastDiscard = null;
    return [{
      ...common,
      type: "unknown_action",
      actionRaw: action,
      tileSlotsRaw: slots,
      tailFlag: readInt32(packet.payload, 20),
    }];
  }

  #decodeDiscard(packet) {
    if (packet.payload.length < 28) return [];
    const seat = readInt32(packet.payload, 0);
    const action = readUInt32(packet.payload, 8);
    const tile = decodePhysicalTile(readUInt16(packet.payload, 12));
    const event = {
      ...packetFields(packet),
      ...seatFields(seat, this.selfSeat),
      type: "discard",
      tile,
      tsumogiri: action === 0x112,
      riichi: action === 0x111,
      afterCall: action === 0xa10,
      actionRaw: action,
      flagsRaw: readUInt32(packet.payload, 4),
      tailFlag: readInt32(packet.payload, 24),
    };
    this.lastDiscard = event;
    return [event];
  }

  #decodeUnconfirmed(packet) {
    return {
      ...packetFields(packet),
      type: "unconfirmed",
      payloadLength: packet.payload.length,
    };
  }

  #decodeHandEnd(packet) {
    this.lastDiscard = null;
    return [{
      ...packetFields(packet),
      type: "hand_end",
      resultLayoutId: packet.messageId,
      detailsKnown: false,
    }];
  }
}

export function decodeMahjongLog(text, options = {}) {
  const decoder = new MahjongPacketDecoder(options);
  const events = [];
  for (const line of text.split(/\r?\n/)) {
    events.push(...decoder.processLine(line));
  }
  return events;
}

function textTile(tile) {
  if (!tile) return "?";
  const copy = Number.isInteger(tile.copy) ? `#${tile.copy}` : "";
  const red = tile.red === true ? " red" : "";
  return `${tile.tile}${copy}${red}`;
}

export function formatEventText(event) {
  const prefix = event.timestamp.slice(11);
  switch (event.type) {
    case "match_start":
      return `${prefix} MATCH START | scores ${formatScores(event.scores)}`;
    case "hand_start":
      return `${prefix} HAND ${event.handIndex} | self=${event.selfSeatName} | dora-marker=${mjaiTile(event.doraIndicator)} | scores ${formatScores(event.scores)} | hand=${event.initialHand.map(mjaiTile).join(" ")}`;
    case "draw":
      return `${prefix} ${event.seatName} DRAW ${textTile(event.tile)}`;
    case "discard": {
      const labels = [];
      if (event.tsumogiri) labels.push("tsumogiri");
      if (event.riichi) labels.push("riichi");
      if (event.afterCall) labels.push("after-call");
      return `${prefix} ${event.seatName} DISCARD ${textTile(event.tile)}${labels.length ? ` (${labels.join(", ")})` : ""}`;
    }
    case "call":
      return `${prefix} ${event.seatName} ${event.kind.toUpperCase()} ${event.calledTile ? textTile(event.calledTile) : "?"} from ${event.fromSeatName ?? "?"} | consumed=${event.consumed.map(textTile).join(" ")}`;
    case "hand_end":
      return `${prefix} HAND END | result-layout=${event.resultLayoutId}`;
    case "unknown_action":
      return `${prefix} ${event.seatName} UNKNOWN ACTION 0x${event.actionRaw.toString(16).toUpperCase()}`;
    case "unconfirmed":
      return `${prefix} UNCONFIRMED message=${event.messageId} length=${event.payloadLength}`;
    default:
      return `${prefix} ${event.type}`;
  }
}

function formatScores(scores) {
  return scores.map((score, seat) => `${SEAT_NAMES[seat]}=${score}`).join(" ");
}

export function summarizeEvents(events) {
  const counts = new Map();
  for (const event of events) counts.set(event.type, (counts.get(event.type) ?? 0) + 1);
  const calls = events.filter((event) => event.type === "call");
  const discards = events.filter((event) => event.type === "discard");
  return {
    events: events.length,
    hands: counts.get("hand_start") ?? 0,
    counts: Object.fromEntries([...counts.entries()].sort(([a], [b]) => a.localeCompare(b))),
    calls: {
      chi: calls.filter((event) => event.kind === "chi").length,
      pon: calls.filter((event) => event.kind === "pon").length,
    },
    discards: {
      riichi: discards.filter((event) => event.riichi).length,
      tsumogiri: discards.filter((event) => event.tsumogiri).length,
      afterCall: discards.filter((event) => event.afterCall).length,
    },
  };
}

function mjaiTile(tile) {
  if (!tile) return "?";
  return tile.red === true ? `${tile.tile}r` : tile.tile;
}

function relativeSeat(absoluteSeat, selfSeat) {
  return (absoluteSeat - selfSeat + 4) % 4;
}

function relativeScores(scores, selfSeat) {
  const rotated = new Array(4);
  for (let absoluteSeat = 0; absoluteSeat < 4; absoluteSeat++) {
    rotated[relativeSeat(absoluteSeat, selfSeat)] = scores[absoluteSeat];
  }
  return rotated;
}

/**
 * Convert decoded packets to Mortal/libriichi's mjai JSON event shape.
 * Player 0 is always the local player; absolute wind seats are rotated each hand.
 */
export class MjaiStreamConverter {
  constructor() {
    this.gameStarted = false;
    this.handOpen = false;
    this.finished = false;
  }

  process(event) {
    if (this.finished) throw new Error("cannot process events after finish()");
    const output = [];
    const startGame = () => {
      if (this.gameStarted) return;
      output.push({ type: "start_game" });
      this.gameStarted = true;
    };

    switch (event.type) {
      case "match_start":
        startGame();
        break;
      case "hand_start": {
        startGame();
        if (this.handOpen) output.push({ type: "end_kyoku" });

        const tehais = Array.from({ length: 4 }, () => Array(13).fill("?"));
        tehais[0] = event.initialHand.map(mjaiTile);
        const roundIndex = Math.floor(event.handIndex / 4);
        output.push({
          type: "start_kyoku",
          bakaze: SEAT_NAMES[roundIndex] ?? "E",
          dora_marker: mjaiTile(event.doraIndicator),
          kyoku: (event.handIndex % 4) + 1,
          honba: 0,
          kyotaku: 0,
          oya: relativeSeat(0, event.selfSeat),
          scores: relativeScores(event.scores, event.selfSeat),
          tehais,
        });
        this.handOpen = true;
        break;
      }
      case "draw":
        if (this.handOpen) {
          output.push({ type: "tsumo", actor: event.relativeSeat, pai: mjaiTile(event.tile) });
        }
        break;
      case "discard":
        if (this.handOpen) {
          if (event.riichi) output.push({ type: "reach", actor: event.relativeSeat });
          output.push({
            type: "dahai",
            actor: event.relativeSeat,
            pai: mjaiTile(event.tile),
            tsumogiri: event.tsumogiri,
          });
          if (event.riichi) output.push({ type: "reach_accepted", actor: event.relativeSeat });
        }
        break;
      case "call":
        if (this.handOpen && event.fromRelativeSeat !== null && event.calledTile) {
          output.push({
            type: event.kind,
            actor: event.relativeSeat,
            target: event.fromRelativeSeat,
            pai: mjaiTile(event.calledTile),
            consumed: event.consumed.map(mjaiTile),
          });
        }
        break;
      case "hand_end":
        if (this.handOpen) {
          output.push({ type: "end_kyoku" });
          this.handOpen = false;
        }
        break;
    }
    return output;
  }

  finish() {
    if (this.finished) return [];
    this.finished = true;
    const output = [];
    if (this.handOpen) output.push({ type: "end_kyoku" });
    if (this.gameStarted) output.push({ type: "end_game" });
    this.handOpen = false;
    return output;
  }
}

export function convertEventsToMjai(events, { finalize = true } = {}) {
  const converter = new MjaiStreamConverter();
  const output = [];
  for (const event of events) output.push(...converter.process(event));
  if (finalize) output.push(...converter.finish());
  return output;
}

function usage() {
  console.log("Usage: node tools/parse-mahjong-packets.mjs <packet-log> [--format text|ndjson|summary|mjai] [--include-unknown] [--limit N]");
}

export function parseArguments(argv) {
  let input = null;
  let format = "text";
  let includeUnknown = false;
  let limit = Number.POSITIVE_INFINITY;

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") return { help: true };
    if (arg === "--include-unknown") {
      includeUnknown = true;
    } else if (arg === "--format") {
      format = argv[++i];
    } else if (arg.startsWith("--format=")) {
      format = arg.slice("--format=".length);
    } else if (arg === "--limit") {
      limit = Number.parseInt(argv[++i], 10);
    } else if (arg.startsWith("--limit=")) {
      limit = Number.parseInt(arg.slice("--limit=".length), 10);
    } else if (arg.startsWith("-")) {
      throw new Error(`Unknown option: ${arg}`);
    } else if (!input) {
      input = arg;
    } else {
      throw new Error(`Unexpected argument: ${arg}`);
    }
  }

  if (!input) throw new Error("Packet log path is required");
  if (!["text", "ndjson", "summary", "mjai"].includes(format)) throw new Error(`Unsupported format: ${format}`);
  if (limit !== Number.POSITIVE_INFINITY && (!Number.isInteger(limit) || limit < 0)) {
    throw new Error("--limit must be a non-negative integer");
  }
  return { input, format, includeUnknown, limit };
}

async function main() {
  let options;
  try {
    options = parseArguments(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    usage();
    process.exitCode = 2;
    return;
  }
  if (options.help) {
    usage();
    return;
  }

  const text = await readFile(options.input, "utf8");
  const events = decodeMahjongLog(text, { includeUnknown: options.includeUnknown });
  if (options.format === "summary") {
    console.log(JSON.stringify(summarizeEvents(events), null, 2));
    return;
  }

  if (options.format === "mjai") {
    for (const event of convertEventsToMjai(events).slice(0, options.limit)) {
      console.log(JSON.stringify(event));
    }
    return;
  }

  for (const event of events.slice(0, options.limit)) {
    console.log(options.format === "ndjson" ? JSON.stringify(event) : formatEventText(event));
  }
}

const entryPoint = process.argv[1] ? pathToFileURL(process.argv[1]).href : null;
if (entryPoint === import.meta.url) {
  main().catch((error) => {
    console.error(error.stack ?? error.message);
    process.exitCode = 1;
  });
}
