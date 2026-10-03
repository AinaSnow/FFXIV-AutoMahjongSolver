"""Read-only archive health audit and optional production-decoder batch replay.

Outputs observations, never counterfactual rank/point loss or protocol certification.
Source archives are not edited. Every selected capture is accounted for, including failures.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import subprocess


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def counts(value, name):
    if not isinstance(value, dict) or any(not isinstance(k, str) or type(v) is not int or v < 0
                                          for k, v in value.items()):
        raise ValueError(f"invalid {name} counts")
    return value


def audit_summary(path):
    row = dict(archive=path.parent.name, summary_sha256=sha256(path), status="insufficient-evidence",
               estimated_point_loss=None, estimated_rank_loss=None)
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    row.update(schema_version=data.get("schema_version"), environment=data.get("environment"),
               started_at=data.get("started_at_utc"), completed_at=data.get("completed_at_utc"))
    if data.get("schema_version") not in (4, 5):
        row["reason"] = "unsupported summary schema; excluded from aggregate"
        return row
    sources = counts(data["decision_health"]["sources"], "decision source")
    outcomes = counts(data["execution_health"]["outcomes"], "execution outcome")
    decisions = data["decision_count"]
    if type(decisions) is not int or decisions < 0 or sum(sources.values()) != decisions:
        raise ValueError("decision source totals do not match decision_count")
    anomalies = {}
    # Counts can overlap. In particular timeout_fallbacks and decision_timeouts must not be added.
    for key, value in dict(failed_actions=data["failed_actions"], timeout_fallbacks=data["timeout_fallbacks"],
                           decision_timeouts=data["mortal"]["decision_timeouts"],
                           missing_outcomes=data["execution_health"]["missing_outcomes"],
                           malformed_lines=data["decision_health"]["malformed_lines"],
                           io_failures=data["decision_health"]["io_failures"]).items():
        if type(value) is not int or value < 0:
            raise ValueError(f"invalid {key}")
        anomalies[key] = value
    if type(data["packet_write_failed"]) is not bool:
        raise ValueError("packet_write_failed is not a boolean")
    anomalies["packet_write_failed"] = int(data["packet_write_failed"])
    row.update(status="audited", decisions=decisions, sources=sources, outcomes=outcomes,
               mortal_coverage=sources.get("mortal", 0) / decisions if decisions else None,
               local_fallback_rate=sources.get("local-fallback", 0) / decisions if decisions else None,
               anomalies=anomalies, final_result_claimed=data.get("final_result_observed"),
               integrity="summary-reported-only",
               outcome_definition=data["execution_health"].get("success_requires"))
    return row


def run_audit(archives, output, captures=None, profile=None, decoder=None, timeout=120):
    summaries = [p / "summary.json" for p in sorted(archives.glob("match-*")) if p.is_dir()]
    if not summaries:
        raise ValueError("no archive summaries; refusing an empty success")
    capture_paths = sorted(captures.glob("capture-*.ndjson")) if captures else []
    if captures and not capture_paths:
        raise ValueError("no captures; refusing an empty replay success")
    output.mkdir(parents=True, exist_ok=False)
    report = dict(schema=1, tool_sha256=sha256(Path(__file__)), runtime_eligible=False, training_ready=False,
                  scope="Observed archive summaries and optional offline protocol replay, not win-rate evidence.",
                  caveats=["Summary counters are not independently verified execution success.",
                           "State change is weaker evidence than server confirmation.",
                           "Non-Mortal decisions include UI recovery and terminal guards, not just fallback.",
                           "Anomaly counters overlap; do not sum them as lost actions.",
                           "Missing scenarios are insufficient evidence; replay never enables a live protocol.",
                           "Recorded subsequent play cannot establish counterfactual point or rank loss."],
                  archives=[], captures=[], errors=[])
    sources, outcomes, anomalies = Counter(), Counter(), Counter()
    by_build = {}
    for path in summaries:
        try:
            row = audit_summary(path)
            report["archives"].append(row)
            if row["status"] == "audited":
                sources.update(row["sources"]); outcomes.update(row["outcomes"]); anomalies.update(row["anomalies"])
                build = (row["environment"] or {}).get("plugin_build_id") or "unknown"
                bucket = by_build.setdefault(build, dict(archives=0, sources=Counter(), anomalies=Counter()))
                bucket["archives"] += 1
                bucket["sources"].update(row["sources"])
                bucket["anomalies"].update(row["anomalies"])
        except (OSError, ValueError, KeyError, TypeError) as error:
            report["errors"].append(dict(source=path.parent.name, stage="summary", reason=str(error)))
    report["by_build"] = by_build
    total = sum(sources.values())
    report["totals"] = dict(selected_archives=len(summaries), audited_archives=sum(r["status"] == "audited" for r in report["archives"]),
                          decisions=total, sources=dict(sources), outcomes=dict(outcomes), anomalies=dict(anomalies),
                          mortal_coverage=sources.get("mortal", 0)/total if total else None,
                          local_fallback_rate=sources.get("local-fallback", 0)/total if total else None)
    for index, path in enumerate(capture_paths):
        row = dict(source=path.name, status="failed")
        report["captures"].append(row)
        try:
            row["source_sha256"] = sha256(path)
            exported = output / f"replay-{index:04d}.json"
            completed = subprocess.run(["dotnet", str(decoder), str(profile), str(path), str(exported)],
                                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
            if completed.returncode:
                raise ValueError((completed.stderr or completed.stdout).strip())
            result = json.loads(exported.read_text(encoding="utf-8-sig"))
            if result["source_sha256"] != row["source_sha256"]:
                raise ValueError("capture changed during audit")
            replay = result["replay"]
            if replay["runtime_eligible"] or replay["schema"] != 1:
                raise ValueError("not a schema 1 offline replay")
            hands = replay["hands"]
            if not hands:
                raise ValueError("no replay hands; insufficient evidence")
            row.update(export=exported.name, profile_sha256=result["profile_sha256"],
                       decoder_sha256=result["decoder_sha256"], unmapped_opcodes=replay["unmapped_opcodes"],
                       caveats=replay["caveats"], hands=[])
            for hand in hands:
                start = next((e["event"] for e in hand["events"] if e["event"]["type"] == "start_kyoku"), {})
                row["hands"].append(dict(number=hand["number"], wind=start.get("bakaze"), kyoku=start.get("kyoku"),
                                         honba=start.get("honba"), boundary=hand["boundary"],
                                         failure=hand["failure"], failure_sequence=hand["failure_sequence"],
                                         offline_model_eligible=hand["offline_model_eligible"]))
            row["status"] = "exported"
        except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
            row["reason"] = str(error)
            report["errors"].append(dict(source=path.name, stage="replay", reason=str(error)))
    report["status"] = "complete" if not report["errors"] and report["totals"]["audited_archives"] == len(summaries) else "incomplete"
    (output / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    lines = ["# 归档审计（只读）", "", f"状态：{report['status']}；审计归档 {report['totals']['audited_archives']}/{len(summaries)}；决策 {total}。",
             "", "## 决策来源", "", "| 来源 | 次数 |", "|---|---:|"]
    lines += [f"| {key} | {value} |" for key, value in sorted(sources.items())]
    lines += ["", "## 异常计数（可能重叠，不可相加）", "", "| 类型 | 次数 |", "|---|---:|"]
    lines += [f"| {key} | {value} |" for key, value in sorted(anomalies.items())]
    lines += ["", f"原始抓包：选择 {len(capture_paths)}，成功导出 {sum(r['status']=='exported' for r in report['captures'])}。",
              "导出成功不等于全部手局可回放；逐局边界、失败和未映射 opcode 见 report.json。",
              "", "点数/名次损失未知。未采到的场景不能标记通过；此报告不开放半庄，不认证协议，也不证明棋力改善。"]
    (output / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archives", type=Path, required=True)
    parser.add_argument("--captures", type=Path)
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--decoder", type=Path, help="Built Mahjong.PacketReplay.dll")
    parser.add_argument("--output", type=Path, required=True, help="New directory, never overwritten")
    parser.add_argument("--timeout-seconds", type=float, default=120)
    args = parser.parse_args()
    if args.timeout_seconds <= 0 or (args.captures and not (args.profile and args.decoder)):
        parser.error("positive timeout required; captures requires profile and decoder")
    try:
        report = run_audit(args.archives, args.output, args.captures, args.profile, args.decoder, args.timeout_seconds)
    except (OSError, ValueError) as error:
        parser.exit(2, f"Audit refused: {error}\n")
    print(json.dumps(dict(status=report["status"], **report["totals"]), ensure_ascii=False))
    return int(report["status"] != "complete")


if __name__ == "__main__":
    raise SystemExit(main())
