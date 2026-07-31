// Run: node tools/test-mortal-bridge.mjs

import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { MortalJsonlProcess, parseBridgeArguments } from "./mortal-bridge.mjs";

assert.deepEqual(
  parseBridgeArguments(["session.log", "--bot-config", "bot.json", "--follow"]),
  {
    input: "session.log",
    botConfig: "bot.json",
    follow: true,
    reactHistory: false,
    pollMs: 200,
  });

const temp = await mkdtemp(join(tmpdir(), "mortal-bridge-test-"));
try {
  const fakeBot = join(temp, "fake-bot.mjs");
  await writeFile(fakeBot, `
    import { createInterface } from "node:readline";
    const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
    for await (const line of lines) {
      const event = JSON.parse(line);
      if (event.can_act === false) continue;
      if (event.type === "tsumo" && event.actor === 0) {
        console.log(JSON.stringify({ type: "dahai", actor: 0, pai: event.pai, tsumogiri: true }));
      }
    }
  `, "utf8");

  const reactions = [];
  const stderr = [];
  const bot = new MortalJsonlProcess(
    { command: process.execPath, args: [fakeBot] },
    {
      onReaction: (reaction) => reactions.push(reaction),
      onStderr: (text) => stderr.push(text),
    });
  bot.start();
  await bot.send({ type: "start_game" }, { canAct: false });
  await bot.send({ type: "tsumo", actor: 0, pai: "5sr" });
  const result = await bot.close();

  assert.equal(result.code, 0);
  assert.equal(bot.sent, 2);
  assert.equal(bot.received, 1);
  assert.deepEqual(reactions, [{ type: "dahai", actor: 0, pai: "5sr", tsumogiri: true }]);
  assert.deepEqual(stderr, []);
} finally {
  await rm(temp, { recursive: true, force: true });
}

console.log("All Mortal bridge assertions passed.");
