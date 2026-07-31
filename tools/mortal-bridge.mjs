// Stream Packet Logger messages into a Mortal-compatible JSONL subprocess.
//
// One-shot replay:
//   node tools/mortal-bridge.mjs Session.log --bot-config mortal-bridge.json
// Live follow (existing events only rebuild state; only appended events can act):
//   node tools/mortal-bridge.mjs Session.log --bot-config mortal-bridge.json --follow

import { spawn } from "node:child_process";
import { once } from "node:events";
import { open, readFile, stat } from "node:fs/promises";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";
import { MahjongPacketDecoder, MjaiStreamConverter } from "./parse-mahjong-packets.mjs";

export class MortalJsonlProcess {
  constructor(config, { onReaction, onStderr } = {}) {
    if (!config || typeof config.command !== "string" || config.command.length === 0) {
      throw new Error("bot config requires a non-empty command");
    }
    if (config.args !== undefined && !Array.isArray(config.args)) {
      throw new Error("bot config args must be an array");
    }

    this.config = config;
    this.onReaction = onReaction ?? (() => {});
    this.onStderr = onStderr ?? ((text) => process.stderr.write(text));
    this.child = null;
    this.exitPromise = null;
    this.sent = 0;
    this.received = 0;
  }

  start() {
    if (this.child) throw new Error("Mortal process already started");
    const child = spawn(this.config.command, this.config.args ?? [], {
      cwd: this.config.cwd,
      env: { ...process.env, ...(this.config.env ?? {}) },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.child = child;

    const stdout = createInterface({ input: child.stdout, crlfDelay: Infinity });
    stdout.on("line", (line) => {
      if (!line.trim()) return;
      let reaction;
      try {
        reaction = JSON.parse(line);
      } catch (error) {
        this.onStderr(`[mortal-bridge] invalid bot JSON: ${error.message}: ${line}\n`);
        return;
      }
      this.received++;
      this.onReaction(reaction, line);
    });
    child.stderr.on("data", (chunk) => this.onStderr(chunk.toString("utf8")));

    this.exitPromise = new Promise((resolve, reject) => {
      child.once("error", reject);
      child.once("exit", (code, signal) => resolve({ code, signal }));
    });
  }

  async send(event, { canAct = true } = {}) {
    if (!this.child?.stdin || this.child.stdin.destroyed) {
      throw new Error("Mortal process stdin is not available");
    }
    const payload = canAct ? event : { ...event, can_act: false };
    this.sent++;
    if (!this.child.stdin.write(`${JSON.stringify(payload)}\n`, "utf8")) {
      await once(this.child.stdin, "drain");
    }
  }

  async close() {
    if (!this.child || !this.exitPromise) return { code: null, signal: null };
    if (!this.child.stdin.destroyed) this.child.stdin.end();
    return this.exitPromise;
  }

  terminate() {
    if (this.child && this.child.exitCode === null) this.child.kill();
  }
}

function usage() {
  console.log("Usage: node tools/mortal-bridge.mjs <packet-log> --bot-config <json> [--follow] [--react-history] [--poll-ms N]");
}

export function parseBridgeArguments(argv) {
  let input = null;
  let botConfig = null;
  let follow = false;
  let reactHistory = false;
  let pollMs = 200;

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") return { help: true };
    if (arg === "--bot-config") botConfig = argv[++i];
    else if (arg.startsWith("--bot-config=")) botConfig = arg.slice("--bot-config=".length);
    else if (arg === "--follow") follow = true;
    else if (arg === "--react-history") reactHistory = true;
    else if (arg === "--poll-ms") pollMs = Number.parseInt(argv[++i], 10);
    else if (arg.startsWith("--poll-ms=")) pollMs = Number.parseInt(arg.slice("--poll-ms=".length), 10);
    else if (arg.startsWith("-")) throw new Error(`Unknown option: ${arg}`);
    else if (!input) input = arg;
    else throw new Error(`Unexpected argument: ${arg}`);
  }

  if (!input) throw new Error("Packet log path is required");
  if (!botConfig) throw new Error("--bot-config is required");
  if (!Number.isInteger(pollMs) || pollMs < 25) throw new Error("--poll-ms must be an integer >= 25");
  return { input, botConfig, follow, reactHistory, pollMs };
}

function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function feedLines(lines, decoder, converter, bot, canAct) {
  let packetEvents = 0;
  let mjaiEvents = 0;
  for (const line of lines) {
    if (!line) continue;
    const decoded = decoder.processLine(line);
    packetEvents += decoded.length;
    for (const packetEvent of decoded) {
      const mjai = converter.process(packetEvent);
      mjaiEvents += mjai.length;
      for (const event of mjai) await bot.send(event, { canAct });
    }
  }
  return { packetEvents, mjaiEvents };
}

async function readAppended(path, offset) {
  const fileStat = await stat(path);
  if (fileStat.size < offset) throw new Error("packet log was truncated while following");
  if (fileStat.size === offset) return { offset, text: "" };

  const length = fileStat.size - offset;
  const buffer = Buffer.alloc(length);
  const handle = await open(path, "r");
  try {
    const { bytesRead } = await handle.read(buffer, 0, length, offset);
    return { offset: offset + bytesRead, text: buffer.subarray(0, bytesRead).toString("utf8") };
  } finally {
    await handle.close();
  }
}

async function main() {
  let options;
  try {
    options = parseBridgeArguments(process.argv.slice(2));
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

  const botConfig = JSON.parse(await readFile(options.botConfig, "utf8"));
  const decoder = new MahjongPacketDecoder();
  const converter = new MjaiStreamConverter();
  const bot = new MortalJsonlProcess(botConfig, {
    onReaction: (_reaction, raw) => process.stdout.write(`${raw}\n`),
  });
  bot.start();

  let stopping = false;
  const stop = () => { stopping = true; };
  process.once("SIGINT", stop);
  process.once("SIGTERM", stop);

  try {
    const initial = await readFile(options.input);
    let offset = initial.length;
    let carry = "";
    const initialText = initial.toString("utf8");
    const initialStats = await feedLines(
      initialText.split(/\r?\n/), decoder, converter, bot,
      !options.follow || options.reactHistory);
    console.error(`[mortal-bridge] replayed ${initialStats.mjaiEvents} mjai events from ${initialStats.packetEvents} decoded packets`);

    if (!options.follow) {
      for (const event of converter.finish()) await bot.send(event, { canAct: false });
    } else {
      console.error(`[mortal-bridge] following ${options.input}`);
      while (!stopping) {
        const appended = await readAppended(options.input, offset);
        offset = appended.offset;
        if (appended.text) {
          const text = carry + appended.text;
          const lines = text.split(/\r?\n/);
          carry = lines.pop() ?? "";
          await feedLines(lines, decoder, converter, bot, true);
        }
        await sleep(options.pollMs);
      }
      if (carry) await feedLines([carry], decoder, converter, bot, true);
      for (const event of converter.finish()) await bot.send(event, { canAct: false });
    }

    const result = await bot.close();
    console.error(`[mortal-bridge] bot exited code=${result.code} signal=${result.signal ?? "none"}; sent=${bot.sent} reactions=${bot.received}`);
    if (result.code !== 0) process.exitCode = result.code ?? 1;
  } catch (error) {
    bot.terminate();
    throw error;
  }
}

const entryPoint = process.argv[1] ? pathToFileURL(process.argv[1]).href : null;
if (entryPoint === import.meta.url) {
  main().catch((error) => {
    console.error(error.stack ?? error.message);
    process.exitCode = 1;
  });
}
