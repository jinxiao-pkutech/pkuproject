"""每个工具的「参数 → CLI 参数」映射与非法参数处理。"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from screenflow_mcp import server

FAKE = str(Path(__file__).parent / "fake_cli.py")


@pytest.fixture(autouse=True)
def record(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> Path:
    path = tmp_path / "record.jsonl"
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    monkeypatch.setenv("SCREENFLOW_DB_DIR", "/tmp/sf-db")
    monkeypatch.setenv("SCREENFLOW_PROFILE", "fixture")
    monkeypatch.setenv("SCREENFLOW_PROJECT", "/tmp/sf-project")
    monkeypatch.setenv("FAKE_CLI_RECORD", str(path))
    monkeypatch.setenv("FAKE_CLI_MODE", "ok")
    monkeypatch.delenv("SCREENFLOW_TIMEOUT_SEC", raising=False)
    return path


def _last_args(record: Path) -> list[str]:
    lines = record.read_text(encoding="utf-8").splitlines()
    assert lines, "fake_cli 没有收到参数"
    return json.loads(lines[-1])


def test_screen_get_maps_terminal(record: Path) -> None:
    server.sf_screen_get("SSAW0903", terminal="customer")
    args = _last_args(record)
    assert args[:5] == ["screen", "get", "--id", "SSAW0903", "--terminal"]
    assert args[5] == "customer"
    assert "--db" in args and "--profile" in args


def test_screen_controls_locale(record: Path) -> None:
    server.sf_screen_controls("SSAW0903", locale="ja")
    args = _last_args(record)
    assert "--locale" in args and args[args.index("--locale") + 1] == "ja"


def test_path_find_defaults(record: Path) -> None:
    server.sf_path_find("SSAW0903", "SSAW0907")
    args = _last_args(record)
    assert args[:4] == ["path", "find", "--from", "SSAW0903"]
    assert "--include-prefix" in args
    assert args[args.index("--detail") + 1] == "summary"
    assert "--k" in args and args[args.index("--k") + 1] == "5"


def test_evidence_get_appends_project(record: Path) -> None:
    server.sf_evidence_get("e_0007ceadfc397defe6d8")
    args = _last_args(record)
    assert "--project" in args
    assert args[args.index("--project") + 1] == "/tmp/sf-project"


def test_message_get_maps_code(record: Path) -> None:
    server.sf_message_get("ERRB022")
    args = _last_args(record)
    assert args[:4] == ["message", "get", "--code", "ERRB022"]


@pytest.mark.parametrize(
    ("tool", "kwargs", "expected"),
    [
        ("sf_screen_get", {"screen_id": "bad id!"}, None),
        ("sf_screen_get", {"screen_id": "SSAW0903", "terminal": "main"}, None),
        ("sf_screen_transitions", {"screen_id": "SSAW0903", "direction": "sideways"}, None),
        ("sf_path_find", {"from_screen": "A", "to_screen": "B", "k": 99}, None),
        ("sf_break_get", {"break_id": "not-a-break"}, None),
        ("sf_symbol_callers", {"symbol_id": "SSAW0903"}, None),
        ("sf_evidence_get", {"evidence_id": "s_123"}, None),
        ("sf_evidence_get", {"evidence_id": "e_123", "context": 11}, None),
    ],
)
def test_invalid_params_return_e_invalid_input(tool: str, kwargs: dict, expected: object) -> None:
    result = getattr(server, tool)(**kwargs)
    assert result["ok"] is False
    assert result["error"]["code"] == "E_INVALID_INPUT"


def test_invalid_params_do_not_call_cli(record: Path) -> None:
    server.sf_screen_get("bad id!")
    assert not record.exists() or record.read_text(encoding="utf-8") == ""
