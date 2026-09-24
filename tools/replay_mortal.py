"""Replay production-decoded captures through the real runner; never connects to the game."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import tomllib


def sha256(path):
    with Path(path).open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


class Runner:
    def __init__(self, command, cwd, stderr, timeout):
        self.timeout = timeout
        self.lines = queue.Queue()
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=stderr, text=True,
                                        encoding="utf-8", bufsize=1,
                                        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self):
        try:
            for line in self.process.stdout:
                self.lines.put(line)
        finally:
            self.lines.put(None)

    def send(self, request):
        self.process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        try:
            line = self.lines.get(timeout=self.timeout)
        except queue.Empty as ex:
            raise TimeoutError("Mortal response deadline exceeded") from ex
        if line is None:
            raise RuntimeError("Mortal exited before acknowledging the input")
        response = json.loads(line)
        if any(response.get(k) != request[k] for k in ("session", "hand", "sequence")):
            raise ValueError("Mortal response correlation mismatch")
        if "error" in response:
            raise RuntimeError(response["error"])
        return response

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            try:
                self.process.stdin.close()
            except BrokenPipeError:
                pass
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=3)
        self.reader.join(timeout=3)
        self.process.stdout.close()


def replay_hand(hand, client, trace, session):
    # A fresh process per hand isolates a bad historical event from all later hands.
    stats = dict(hand=hand["number"], start_sequence=hand["start_sequence"],
                 events=0, reactions=0, failed_packet=None, failure=None, status="passed")
    events = hand["events"] + [dict(packet_sequence=None, event=dict(type="end_game"))]
    for sequence, row in enumerate(events, 1):
        event = dict(row["event"])
        # Reactions are diagnostic only. The next input always follows the recorded game,
        # never the hypothetical recommendation; this is not a counterfactual match.
        event["can_act"] = event["type"] not in ("start_game", "start_kyoku", "end_kyoku", "end_game")
        request = dict(session=session, hand=hand["number"], sequence=sequence, event=event)
        started = time.monotonic()
        try:
            response = client.send(request)
        except Exception as ex:
            stats.update(status="failed", failed_packet=row["packet_sequence"],
                         failed_event=event["type"], failure=f"{type(ex).__name__}: {ex}")
            break
        stats["events"] += 1
        stats["reactions"] += int(response.get("reaction") is not None)
        trace.write(json.dumps(dict(session=session, hand=hand["number"], packet_sequence=row["packet_sequence"],
                                    event=event, response=response,
                                    elapsed_ms=(time.monotonic()-started)*1000)) + "\n")
        trace.flush()
    return stats


def model_manifest(mortal_dir):
    config_path = Path(os.environ.get("MORTAL_CFG", "config.toml"))
    if not config_path.is_absolute():
        config_path = mortal_dir / config_path
    with config_path.open("rb") as f:
        config = tomllib.load(f)
    weights = Path(config["control"]["state_file"])
    if not weights.is_absolute():
        weights = mortal_dir / weights
    return dict(config_sha256=sha256(config_path), weights_sha256=sha256(weights),
                libriichi_sha256=sha256(mortal_dir / "libriichi.so"),
                runner_sha256=sha256(Path(__file__).with_name("mortal_runner.py")))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--input", type=Path, action="append", required=True)
    p.add_argument("--mortal-dir", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True, help="New directory; existing results are never overwritten")
    p.add_argument("--timeout-seconds", type=float, default=60)
    args = p.parse_args()
    if args.timeout_seconds <= 0:
        p.error("timeout must be positive")
    args.mortal_dir = args.mortal_dir.resolve()
    inputs = [(path, json.loads(path.read_text(encoding="utf-8-sig"))) for path in args.input]
    if not inputs or any(data["replay"]["schema"] != 1 or data["replay"]["runtime_eligible"] for _, data in inputs):
        p.error("expected explicitly offline replay export schema 1")
    manifest = model_manifest(args.mortal_dir)
    args.output.mkdir(parents=True, exist_ok=False)
    report = dict(schema=1, runtime_eligible=False, model=manifest, inputs=[], hands=[],
                  scope="Model compatibility on recorded, mapped events. Not protocol certification or win-rate evidence.")
    runner = Path(__file__).with_name("mortal_runner.py").resolve()
    with (args.output / "reactions.ndjson").open("x", encoding="utf-8") as trace:
        for index, (path, data) in enumerate(inputs):
            report["inputs"].append(dict(export_sha256=sha256(path), source_sha256=data["source_sha256"],
                                        decoder_sha256=data["decoder_sha256"], caveats=data["replay"]["caveats"],
                                        unmapped_opcodes=data["replay"]["unmapped_opcodes"]))
            for hand in data["replay"]["hands"]:
                if not hand["offline_model_eligible"] or hand["failure"] or hand["boundary"] != "result":
                    report["hands"].append(dict(input=index, hand=hand["number"], status="skipped",
                                                reason=hand["failure"] or hand["boundary"],
                                                failed_packet=hand["failure_sequence"]))
                    continue
                session = f"offline-{index}-{hand['number']}"
                with (args.output / f"{session}.stderr.log").open("x", encoding="utf-8") as stderr:
                    client = Runner([sys.executable, str(runner)], args.mortal_dir, stderr, args.timeout_seconds)
                    try:
                        result = replay_hand(hand, client, trace, session)
                    finally:
                        client.close()
                report["hands"].append(dict(input=index, **result))
                print(f"input={index} hand={hand['number']} {result['status']} events={result['events']} reactions={result['reactions']}", flush=True)
            (args.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    passed = sum(h["status"] == "passed" for h in report["hands"])
    failed = sum(h["status"] == "failed" for h in report["hands"])
    print(f"Offline only: passed={passed}, failed={failed}, skipped={len(report['hands'])-passed-failed}")
    return int(failed > 0 or passed == 0)


if __name__ == "__main__":
    raise SystemExit(main())
