"""--selftest：成功退出 0，任一步失败退出 1。"""

from __future__ import annotations

from pathlib import Path

import pytest

from screenflow_mcp import server

FAKE = str(Path(__file__).parent / "fake_cli.py")


@pytest.fixture(autouse=True)
def _env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SCREENFLOW_CLI", FAKE)
    monkeypatch.setenv("SCREENFLOW_DB_DIR", "/tmp/sf-db")
    monkeypatch.setenv("SCREENFLOW_PROFILE", "fixture")
    monkeypatch.setenv("SCREENFLOW_PROJECT", "/tmp/sf-project")
    monkeypatch.setenv("FAKE_CLI_MODE", "ok")


def test_selftest_succeeds(capsys: pytest.CaptureFixture[str]) -> None:
    assert server.run_selftest("SSAW0903", "SSAW0907") == 0
    output = capsys.readouterr().out
    assert "ok=True" in output


def test_selftest_fails_when_a_step_fails(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    monkeypatch.setenv("FAKE_CLI_MODE", "error-valid")
    assert server.run_selftest("SSAW0903", "SSAW0907") == 1
    output = capsys.readouterr().out
    assert "ok=False" in output
