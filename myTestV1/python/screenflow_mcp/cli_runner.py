"""子进程调用层：把参数列表交给 ScreenFlow CLI，返回解析后的信封 dict。

只有这里接触子进程与 JSON 解析，不含任何领域解析逻辑（不读数据库、不读源码）。
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

_STDERR_TAIL = 2000


def profile() -> str:
    return os.environ.get("SCREENFLOW_PROFILE", "android-onsite")


def db_path() -> str:
    db_dir = os.environ.get("SCREENFLOW_DB_DIR")
    if not db_dir:
        return f".screenflow/{profile()}.db"
    return os.path.join(db_dir, f"{profile()}.db")


def timeout_seconds() -> int:
    value = os.environ.get("SCREENFLOW_TIMEOUT_SEC", "120")
    try:
        parsed = int(value)
    except ValueError:
        return 120
    return parsed if parsed > 0 else 120


def _cli_prefix(cli: str) -> list[str]:
    """SCREENFLOW_CLI 可以是 .exe、.dll（前置 dotnet）或 .py（前置当前解释器）。"""
    if cli.lower().endswith(".dll"):
        return ["dotnet", cli]
    if cli.lower().endswith(".py"):
        return [sys.executable, cli]
    return [cli]


def command(args: list[str]) -> list[str]:
    cli = os.environ.get("SCREENFLOW_CLI")
    if not cli:
        raise RuntimeError("SCREENFLOW_CLI 环境变量未设置。")

    cmd = _cli_prefix(cli) + list(args)
    cmd += ["--db", db_path(), "--profile", profile()]

    # evidence get 从工程实时读源码片段，需要工程根。
    if list(args)[:2] == ["evidence", "get"]:
        project = os.environ.get("SCREENFLOW_PROJECT")
        if project:
            cmd += ["--project", project]

    return cmd


def _failure(message: str) -> dict:
    return {"ok": False, "error": {"code": "E_CLI_FAILURE", "message": message}, "meta": {}}


def run(args: list[str]) -> dict:
    """执行 CLI 并返回信封 dict。

    退出码非 0 但 stdout 是合法信封时原样返回；stdout 非法时返回 ``E_CLI_FAILURE``。
    """
    try:
        cmd = command(args)
    except RuntimeError as exc:
        return _failure(str(exc))

    try:
        process = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=timeout_seconds(),
        )
    except subprocess.TimeoutExpired:
        return _failure(f"CLI 超时（{timeout_seconds()}s）。")
    except FileNotFoundError as exc:
        return _failure(f"CLI 无法启动：{exc}")
    except OSError as exc:
        return _failure(f"CLI 调用失败：{exc}")

    return parse(process.stdout, process.stderr)


def parse(stdout: str, stderr: str) -> dict:
    try:
        envelope = json.loads(stdout)
    except (json.JSONDecodeError, TypeError):
        return _failure(stderr[-_STDERR_TAIL:])

    if not isinstance(envelope, dict) or "ok" not in envelope:
        return _failure(stderr[-_STDERR_TAIL:])

    return envelope
