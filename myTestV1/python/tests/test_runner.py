"""cli_runner 的参数组装与失败处理。"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

from screenflow_mcp import cli_runner

FAKE = str(Path(__file__).parent / "fake_cli.py")


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", sys.executable)
    monkeypatch.setenv("SCREENFLOW_DB_DIR", "/tmp/sf-db")
    monkeypatch.setenv("SCREENFLOW_PROFILE", "fixture")
    monkeypatch.setenv("SCREENFLOW_PROJECT", "/tmp/sf-project")
    monkeypatch.delenv("FAKE_CLI_MODE", raising=False)
    monkeypatch.delenv("FAKE_CLI_RECORD", raising=False)


def test_cli_exe_is_invoked_directly() -> None:
    assert cli_runner._cli_prefix("C:/bin/screenflow.exe") == ["C:/bin/screenflow.exe"]


def test_cli_dll_gets_dotnet_prefix() -> None:
    assert cli_runner._cli_prefix("C:/bin/screenflow.dll") == ["dotnet", "C:/bin/screenflow.dll"]


def test_cli_py_gets_python_prefix() -> None:
    assert cli_runner._cli_prefix("fake_cli.py") == [sys.executable, "fake_cli.py"]


def test_command_appends_db_and_profile(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", "screenflow.exe")
    cmd = cli_runner.command(["screen", "get", "--id", "SSAW0903"])
    assert cmd[:5] == ["screenflow.exe", "screen", "get", "--id", "SSAW0903"]
    assert cmd[5] == "--db"
    assert cmd[6] == cli_runner.db_path()
    assert cmd[7] == "--profile"
    assert cmd[8] == "fixture"


def test_evidence_get_appends_project(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", "screenflow.exe")
    cmd = cli_runner.command(["evidence", "get", "--id", "e_abc123"])
    assert "--project" in cmd
    assert cmd[-1] == "/tmp/sf-project"


def test_valid_stdout_is_returned(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    envelope = cli_runner.run(["screen", "get", "--id", "SSAW0903"])
    assert envelope["ok"] is True


def test_nonzero_exit_with_valid_stdout_is_passed_through(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    monkeypatch.setenv("FAKE_CLI_MODE", "error-valid")
    envelope = cli_runner.run(["break", "get", "--id", "b_1234"])
    assert envelope["ok"] is False
    assert envelope["error"]["code"] == "E_NOT_FOUND"


def test_invalid_stdout_returns_e_cli_failure(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    monkeypatch.setenv("FAKE_CLI_MODE", "error-invalid")
    envelope = cli_runner.run(["index", "status"])
    assert envelope["ok"] is False
    assert envelope["error"]["code"] == "E_CLI_FAILURE"
    assert "boom" in envelope["error"]["message"]


def test_timeout_returns_e_cli_failure(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    monkeypatch.setenv("FAKE_CLI_MODE", "sleep")
    monkeypatch.setenv("FAKE_CLI_SLEEP", "5")
    monkeypatch.setenv("SCREENFLOW_TIMEOUT_SEC", "1")
    envelope = cli_runner.run(["index", "status"])
    assert envelope["ok"] is False
    assert envelope["error"]["code"] == "E_CLI_FAILURE"
