#!/usr/bin/env python3
"""本机验收脚本（05 §6.2）：逐项执行 A-01 … A-19，比较期望，生成 summary.md / summary.json。

用法：
  python acceptance/run_acceptance.py --project <P> --cli <C> --expect <E>
      [--profiles a,b] [--skip A-02,…]
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

try:
    import psutil  # type: ignore

    HAS_PSUTIL = True
except ImportError:  # 02 §1.2 不允许自行安装 psutil
    HAS_PSUTIL = False

if __package__ in (None, ""):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from acceptance import strip_comments  # noqa: E402

CLI_TIMEOUT = 300


# --------------------------------------------------------------------------- DSL


def resolve(node: object, path: str) -> list[object]:
    """JSON 路径解析：``data.paths[0].hops[0].edges``，``[*]`` 表示数组全部元素。"""
    current: list[object] = [node]
    for token in _path_tokens(path):
        if token[0] == "prop":
            prop = token[1]
            nxt: list[object] = []
            for cur in current:
                if isinstance(cur, dict) and prop in cur:
                    nxt.append(cur[prop])
                elif isinstance(cur, list):
                    for item in cur:
                        if isinstance(item, dict) and prop in item:
                            nxt.append(item[prop])
            current = nxt
        else:
            index = token[1]
            nxt = []
            if index == "*":
                for cur in current:
                    if isinstance(cur, list):
                        nxt.extend(cur)
            else:
                for cur in current:
                    if isinstance(cur, list) and -len(cur) <= index < len(cur):
                        nxt.append(cur[index])
            current = nxt
    return current


def _path_tokens(path: str) -> list[tuple[str, object]]:
    tokens: list[tuple[str, object]] = []
    i = 0
    n = len(path)
    if n == 0:
        return tokens

    # 开头允许直接是属性名。
    if path[0] not in ".[":
        j = i
        while j < n and (path[j].isalnum() or path[j] in "_-"):
            j += 1
        tokens.append(("prop", path[i:j]))
        i = j

    while i < n:
        if path[i] == ".":
            i += 1
            j = i
            while j < n and (path[j].isalnum() or path[j] in "_-"):
                j += 1
            if j == i:
                raise ValueError(f"非法 JSON 路径：{path}")
            tokens.append(("prop", path[i:j]))
            i = j
        elif path[i] == "[":
            j = path.index("]", i)
            inner = path[i + 1 : j]
            if inner == "*":
                tokens.append(("index", "*"))
            else:
                tokens.append(("index", int(inner)))
            i = j + 1
        else:
            raise ValueError(f"非法 JSON 路径：{path}")
    return tokens


def assert_matches(node: object, assertion: dict) -> bool:
    """断言 DSL：``exists / equals / contains / count>= / allNot`` + ``and/or/not``。"""
    op = assertion.get("op")
    if op == "and":
        return all(assert_matches(node, rule) for rule in assertion["rules"])
    if op == "or":
        return any(assert_matches(node, rule) for rule in assertion["rules"])
    if op == "not":
        return not assert_matches(node, assertion["rule"])

    path = assertion["path"]
    values = resolve(node, path)

    if op == "exists":
        return any(value is not None for value in values)
    if op == "equals":
        return len(values) > 0 and all(value == assertion["value"] for value in values)
    if op == "contains":
        expected = assertion.get("value")
        where = assertion.get("where")
        for value in values:
            if isinstance(value, list):
                if where is not None:
                    if any(assert_matches(item, where) for item in value):
                        return True
                elif any(item == expected for item in value):
                    return True
            elif isinstance(value, str) and expected is not None and str(expected) in value:
                return True
        return False
    if op == "count>=":
        return any(isinstance(value, list) and len(value) >= assertion["value"] for value in values)
    if op == "allNot":
        expected = assertion.get("value")
        for value in values:
            if isinstance(value, list):
                if any(item == expected for item in value):
                    return False
            elif value == expected:
                return False
        return True

    raise ValueError(f"未知断言 op：{op}")


# --------------------------------------------------------------------------- CLI


def cli_command(cli: str, args: list[str]) -> list[str]:
    if cli.lower().endswith(".dll"):
        return ["dotnet", cli, *args]
    return [cli, *args]


def run_cli(cli: str, args: list[str], timeout: int = CLI_TIMEOUT) -> dict:
    started = time.monotonic()
    try:
        process = subprocess.run(
            cli_command(cli, args),
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return {
            "returncode": -1,
            "stdout": "",
            "stderr": f"CLI 超时（{timeout}s）",
            "elapsed_ms": (time.monotonic() - started) * 1000,
        }

    return {
        "returncode": process.returncode,
        "stdout": process.stdout,
        "stderr": process.stderr,
        "elapsed_ms": (time.monotonic() - started) * 1000,
    }


def cli_json(cli: str, args: list[str], timeout: int = CLI_TIMEOUT) -> dict:
    run = run_cli(cli, args, timeout)
    if run["returncode"] != 0:
        raise RuntimeError(f"CLI 失败：{' '.join(args)} → {run['stderr'][-500:]}")
    return json.loads(run["stdout"])


# --------------------------------------------------------------------------- context


class Context:
    def __init__(self, project: str, cli: str, profiles: list[str], results_dir: Path, item: dict):
        self.project = project
        self.cli = cli
        self.profiles = profiles
        self.results_dir = results_dir
        self.item = item

    def db_path(self, profile: str) -> str:
        return str(self.results_dir / "db" / f"{profile}.db")

    def profile(self) -> str:
        return self.item.get("profile") or self.profiles[0]


# --------------------------------------------------------------------------- 命令项执行


def _substitute(args: list[str], ctx: Context, profile: str) -> list[str]:
    return [
        arg.replace("{project}", ctx.project)
        .replace("{profile}", profile)
        .replace("{db}", ctx.db_path(profile))
        for arg in args
    ]


def _run_step(step: dict, ctx: Context, profile: str) -> dict:
    args = _substitute(step["command"], ctx, profile)
    run = run_cli(ctx.cli, args, timeout=step.get("timeout", CLI_TIMEOUT))
    envelope = None
    parse_error = None
    try:
        envelope = json.loads(run["stdout"])
    except json.JSONDecodeError as exc:
        parse_error = f"stdout 不是合法 JSON：{exc}"

    passed = False
    warn = False
    detail = ""
    if parse_error is None and envelope is not None:
        assertion = step.get("assert")
        if assertion is None:
            passed = bool(envelope.get("ok"))
        else:
            passed = assert_matches(envelope, assertion)
            detail = json.dumps(assertion, ensure_ascii=False)

        if not passed and "warn_if" in step:
            warn = assert_matches(envelope, step["warn_if"])

    return {
        "passed": passed,
        "warn": warn,
        "command": " ".join(args),
        "returncode": run["returncode"],
        "elapsed_ms": run["elapsed_ms"],
        "parse_error": parse_error,
        "detail": detail,
        "raw": run["stdout"],
    }


def _execute_command_item(ctx: Context) -> dict:
    steps = ctx.item.get("steps")
    if steps is None:
        steps = [ctx.item]

    step_results = []
    for step in steps:
        profile = step.get("profile", ctx.profile())
        step_results.append(_run_step(step, ctx, profile))

    passed = all(step["passed"] for step in step_results)
    warn = (not passed) and any(step.get("warn") for step in step_results)
    detail = "; ".join(
        f"{step['command']} → exit {step['returncode']} ({step['elapsed_ms']:.0f}ms)"
        for step in step_results
    )
    return {
        "passed": passed,
        "warn": warn,
        "detail": detail,
        "steps": step_results,
        "raw": step_results[-1]["raw"] if step_results else "",
    }


# --------------------------------------------------------------------------- 交叉核对


def _stripped_files(project: str) -> list[tuple[str, str]]:
    """(工程相对路径, 去注释文本) 列表。"""
    root = Path(project)
    result = []
    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        result.append((rel, strip_comments.strip_comments(path.read_text(encoding="utf-8", errors="replace"))))
    return result


def _count_pattern(files: list[tuple[str, str]], pattern: str) -> list[tuple[str, int]]:
    hits = []
    for rel, text in files:
        for lineno, line in enumerate(text.splitlines(), start=1):
            if pattern in line:
                hits.append((rel, lineno))
    return hits


def _cross_tcp_sends(ctx: Context) -> dict:
    files = _stripped_files(ctx.project)
    n1_lines = _count_pattern(files, "SslSend(") + _count_pattern(files, "SslContentsSend(")
    stats = cli_json(ctx.cli, ["debug", "stats", "--db", ctx.db_path(ctx.profile())])
    n2 = stats["data"]["domainEdgeKinds"].get("messageSend", 0)
    definitions = [(rel, line) for rel, line in n1_lines if line in _definition_lines(rel, files)]
    passed = n2 <= len(n1_lines)
    return {
        "passed": passed,
        "detail": f"N1={len(n1_lines)}（含定义行 {len(definitions)}）、N2={n2}、差值={len(n1_lines) - n2}；定义行={definitions}",
        "raw": "",
    }


def _definition_lines(rel: str, files: list[tuple[str, str]]) -> set[int]:
    for file_rel, text in files:
        if file_rel != rel:
            continue
        return {
            lineno
            for lineno, line in enumerate(text.splitlines(), start=1)
            if re.search(r"\b(public|private|protected|internal)?\s*(async\s+)?Task\b.*\bSsl(Contents)?Send\(", line)
        }
    return set()


def _cross_remote_display(ctx: Context) -> dict:
    files = _stripped_files(ctx.project)
    f1 = {rel for rel, text in files if "ScreenID.SSAW0903.Id" in text}
    sends = cli_json(ctx.cli, ["debug", "sends", "--target-screen", "SSAW0903", "--db", ctx.db_path(ctx.profile())])
    f2 = {
        item["from"]["file"]
        for item in sends["data"]
        if isinstance(item.get("from"), dict) and item["from"].get("file")
    }
    missing = sorted(f1 - f2)
    return {
        "passed": not missing,
        "detail": f"F1={sorted(f1)}、F2={sorted(f2)}、缺失={missing}",
        "raw": "",
    }


def _cross_keyseq(ctx: Context) -> dict:
    files = _stripped_files(ctx.project)
    m1 = len(_count_pattern(files, "new KeySeqTableRecord("))
    stats = cli_json(ctx.cli, ["debug", "stats", "--db", ctx.db_path(ctx.profile())])
    m2 = stats["data"]["counts"].get("keyseq_transitions", 0)
    return {"passed": m1 == m2, "detail": f"M1={m1}、M2={m2}", "raw": ""}


def _cross_screens(ctx: Context) -> dict:
    files = _stripped_files(ctx.project)
    d1 = len(_count_pattern(files, "public static readonly ScreenID "))
    r1 = len(_count_pattern(files, "{ ScreenID."))
    stats = cli_json(ctx.cli, ["debug", "stats", "--db", ctx.db_path(ctx.profile())])
    d2 = stats["data"]["counts"].get("screens", 0)

    if "registered" in ctx.item:
        r2 = ctx.item["registered"]
        passed = d1 == d2 and r1 == r2
        detail = f"定义 D1={d1}/D2={d2}、注册表 {r1}/registered={r2}"
    else:
        passed = d1 == d2
        detail = f"定义 D1={d1}/D2={d2}、注册表出现 {r1} 处（未核对 registered）"

    return {"passed": passed, "detail": detail, "raw": ""}


def _find_preprocessor_blocks(project: str, symbol: str) -> list[tuple[str, int, int]]:
    """找到所有 ``#if <symbol> … #endif`` 区间（支持嵌套 #if）。"""
    blocks: list[tuple[str, int, int]] = []
    root = Path(project)
    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        stack: list[int] = []
        depth = 0
        for idx, line in enumerate(lines, start=1):
            stripped = line.strip()
            if stripped.startswith("#if"):
                depth += 1
                if re.match(rf"^#if\s+{symbol}\b", stripped):
                    stack.append(idx)
            elif stripped.startswith("#endif"):
                if stack:
                    start = stack.pop()
                    if depth == 1:
                        blocks.append((rel, start, idx))
                depth -= 1
    return blocks


def _cross_teamui(ctx: Context) -> dict:
    blocks = _find_preprocessor_blocks(ctx.project, "TeamUI")
    if not blocks:
        return {"passed": True, "detail": "没有 #if TeamUI 区间", "raw": ""}

    all_empty = True
    details = []
    for rel, start, end in blocks:
        result = cli_json(
            ctx.cli,
            ["debug", "callsites", "--file", rel, "--from-line", str(start), "--to-line", str(end), "--db", ctx.db_path(ctx.profile())],
        )
        data = result["data"]
        empty = not (data.get("callsites") or data.get("assigns") or data.get("creations"))
        all_empty = all_empty and empty
        details.append(f"{rel}:{start}-{end} → {'空' if empty else '非空'}")

    return {"passed": all_empty, "detail": "; ".join(details), "raw": ""}


def _cross_comment_exclusion(ctx: Context) -> dict:
    rel = ctx.item["file"]
    line = ctx.item["line"]
    result = cli_json(
        ctx.cli,
        ["debug", "callsites", "--file", rel, "--from-line", str(line), "--to-line", str(line), "--db", ctx.db_path(ctx.profile())],
    )
    data = result["data"]
    empty = not (data.get("callsites") or data.get("assigns") or data.get("creations"))
    return {"passed": empty, "detail": f"{rel}:{line} → {'空' if empty else '非空'}", "raw": ""}


def _cross_determinism(ctx: Context) -> dict:
    db1 = ctx.results_dir / "det-a.db"
    db2 = ctx.results_dir / "det-b.db"
    dump1 = ctx.results_dir / "det-a.tsv"
    dump2 = ctx.results_dir / "det-b.tsv"

    profile = ctx.profile()
    for db in (db1, db2):
        run = run_cli(
            ctx.cli,
            ["index", "build", "--project", ctx.project, "--profile", profile, "--db", str(db)],
        )
        if run["returncode"] != 0:
            return {"passed": False, "detail": run["stderr"][-300:], "raw": run["stdout"]}

    for db, dump in ((db1, dump1), (db2, dump2)):
        run = run_cli(ctx.cli, ["index", "dump", "--db", str(db), "--profile", profile, "--out", str(dump)])
        if run["returncode"] != 0:
            return {"passed": False, "detail": run["stderr"][-300:], "raw": run["stdout"]}

    passed = dump1.read_bytes() == dump2.read_bytes()
    return {
        "passed": passed,
        "detail": f"dump 两次字节{'相同' if passed else '不同'}",
        "raw": "",
    }


def _cross_performance(ctx: Context) -> dict:
    run = run_cli(
        ctx.cli,
        ["path", "find", "--from", "SSAW0903", "--to", "SSAW0907", "--k", "5", "--db", ctx.db_path(ctx.profile())],
        timeout=30,
    )
    elapsed = run["elapsed_ms"]
    passed = run["returncode"] == 0 and elapsed < 10_000
    return {
        "passed": passed,
        "detail": f"path find K=5 耗时 {elapsed:.0f}ms",
        "raw": run["stdout"],
    }


def _cross_mcp_selftest(ctx: Context) -> dict:
    env = dict(os.environ)
    env["SCREENFLOW_CLI"] = ctx.cli
    env["SCREENFLOW_DB_DIR"] = str(ctx.results_dir / "db")
    env["SCREENFLOW_PROFILE"] = ctx.profile()
    env["SCREENFLOW_PROJECT"] = ctx.project
    run = subprocess.run(
        [sys.executable, "-m", "screenflow_mcp", "--selftest", "--from", "SSAW0903", "--to", "SSAW0907"],
        capture_output=True, text=True, encoding="utf-8", timeout=120, env=env,
    )
    return {
        "passed": run.returncode == 0,
        "detail": run.stdout.strip().replace("\n", " / "),
        "raw": run.stdout,
    }


def _cross_manual(ctx: Context) -> dict:
    return {
        "passed": False,
        "warn": True,
        "detail": "人工执行：部署 deploy/ 后，在 Copilot 选择 screen-flow 并输入 SSAW0903 SSAW0907。",
        "raw": "",
    }


CROSS_FUNCTIONS = {
    "tcp_sends": _cross_tcp_sends,
    "remote_display": _cross_remote_display,
    "keyseq": _cross_keyseq,
    "screens": _cross_screens,
    "teamui": _cross_teamui,
    "comment_exclusion": _cross_comment_exclusion,
    "determinism": _cross_determinism,
    "performance": _cross_performance,
    "mcp_selftest": _cross_mcp_selftest,
    "manual": _cross_manual,
}


def _execute_cross_item(ctx: Context) -> dict:
    name = ctx.item["cross"]
    function = CROSS_FUNCTIONS.get(name)
    if function is None:
        return {"passed": False, "detail": f"未知交叉核对：{name}", "steps": [], "raw": ""}

    result = function(ctx)
    result.setdefault("steps", [])
    return result


# --------------------------------------------------------------------------- 主流程


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="ScreenFlow 本机验收（05 §6.2）")
    parser.add_argument("--project", required=True)
    parser.add_argument("--cli", required=True)
    parser.add_argument("--expect", required=True)
    parser.add_argument("--profiles", default="android-onsite,windows-editor")
    parser.add_argument("--skip", default="")
    args = parser.parse_args(argv)

    profiles = [p for p in args.profiles.split(",") if p]
    skip = {s for s in args.skip.split(",") if s}

    expectations = json.loads(Path(args.expect).read_text(encoding="utf-8"))
    items = expectations["items"]

    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    results_dir = Path("acceptance") / "results" / stamp
    (results_dir / "db").mkdir(parents=True, exist_ok=True)

    summary_rows = []
    all_ok = True

    for item in items:
        item_id = item["id"]
        if item_id in skip:
            summary_rows.append((item_id, "SKIP", "跳过"))
            continue

        ctx = Context(args.project, args.cli, profiles, results_dir, item)
        if "cross" in item:
            result = _execute_cross_item(ctx)
        else:
            result = _execute_command_item(ctx)

        if result.get("warn"):
            status = "WARN"
        elif result["passed"]:
            status = "PASS"
        else:
            status = "FAIL"

        if status == "FAIL":
            all_ok = False

        summary_rows.append((item_id, status, result.get("detail", "")))
        item_out = results_dir / f"{item_id}.json"
        item_out.write_text(
            json.dumps(
                {
                    "id": item_id,
                    "status": status,
                    "command": result.get("steps", []) or result.get("detail", ""),
                    "detail": result.get("detail", ""),
                    "raw": result.get("raw", ""),
                },
                ensure_ascii=False,
                indent=2,
            ),
            encoding="utf-8",
        )

    _write_summary(results_dir, summary_rows)

    print(f"结果目录：{results_dir}")
    for item_id, status, detail in summary_rows:
        print(f"{item_id}: {status} {detail}")

    return 0 if all_ok else 1


def _write_summary(results_dir: Path, rows: list[tuple[str, str, str]]) -> None:
    md_lines = ["# 验收摘要", "", "| ID | 结果 | 说明 |", "|---|---|---|"]
    for item_id, status, detail in rows:
        md_lines.append(f"| {item_id} | {status} | {detail} |")

    (results_dir / "summary.md").write_text("\n".join(md_lines) + "\n", encoding="utf-8")
    (results_dir / "summary.json").write_text(
        json.dumps(
            {"items": [{"id": i, "status": s, "detail": d} for i, s, d in rows]},
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )


if __name__ == "__main__":
    raise SystemExit(main())
