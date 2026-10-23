"""T22 部署文件检查：agent frontmatter、规则与报告模板、Skill 术语覆盖、mcp.json 合法性。"""

from __future__ import annotations

import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

AGENT = REPO / "deploy" / ".github" / "agents" / "screen-flow.agent.md"
SKILL = REPO / "deploy" / ".github" / "skills" / "screen-flow" / "SKILL.md"
MCP_JSON = REPO / "deploy" / ".vscode" / "mcp.json"

TOOLS = [
    "sf_index_status",
    "sf_screen_get",
    "sf_screen_controls",
    "sf_screen_constraints",
    "sf_screen_transitions",
    "sf_path_find",
    "sf_path_prefix",
    "sf_break_list",
    "sf_break_get",
    "sf_symbol_find",
    "sf_symbol_callers",
    "sf_symbol_callees",
    "sf_evidence_get",
    "sf_text_get",
    "sf_message_get",
]

CATEGORIES = [
    "ExternalHardware",
    "ExternalMicroservice",
    "TerminalTcpCurrentScreen",
    "NativeInterop",
    "DatabaseMediated",
    "DiAmbiguous",
    "DeferredExecution",
    "KeySeqStageDependent",
    "Unknown",
]

REASONS = [
    "NoSourceForExternalSystem",
    "RuntimeStateDependentDispatch",
    "MultipleDiRegistrations",
    "ReflectionConstruction",
    "DeferredExecution",
    "DataMediatedByDatabase",
    "StringKeyedDispatchUnmatched",
    "NativeInterop",
    "UnresolvedSymbol",
    "UnresolvedConstantArgument",
    "ScreenNotRegistered",
    "TruncatedByDepthLimit",
    "TruncatedByCandidateLimit",
]

RULE_PHRASES = [
    "事实只来自工具结果",
    "不读源码文件，不推测",
    "确认度原样转述",
    "截断必须告知",
    "索引状态必须告知",
    "断链必须逐条列出",
    "调用次数上限 25 次",
]

SECTION_TITLES = [
    "## 1. 结论摘要",
    "## 2. 前置路径",
    "## 3. 路径一览",
    "## 4. 路径详情",
    "## 5. 断链清单",
    "## 6. 路径上画面的输入约束",
    "## 7. 未解析与截断事项",
]


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_agent_frontmatter() -> None:
    text = _read(AGENT)
    assert text.startswith("---\n")
    frontmatter = text.split("---", 2)[1]
    assert "name: screen-flow" in frontmatter
    assert "tools: ['screenflow/*']" in frontmatter
    assert "argument-hint:" in frontmatter


def test_agent_contains_rules_and_template() -> None:
    text = _read(AGENT)
    for phrase in RULE_PHRASES:
        assert phrase in text, f"agent 缺少规则关键句：{phrase}"
    for title in SECTION_TITLES:
        assert title in text, f"agent 缺少报告章节标题：{title}"


def test_skill_covers_all_tools_and_terms() -> None:
    text = _read(SKILL)
    for tool in TOOLS:
        assert tool in text, f"SKILL 缺少工具：{tool}"
    for category in CATEGORIES:
        assert category in text, f"SKILL 缺少断链类别：{category}"
    for reason in REASONS:
        assert reason in text, f"SKILL 缺少断链原因码：{reason}"


def test_mcp_json_valid() -> None:
    data = json.loads(_read(MCP_JSON))
    assert "servers" in data
    server = data["servers"]["screenflow"]
    assert server["type"] == "stdio"
    assert server["command"] == "python"
    assert "-m" in server["args"] and "screenflow_mcp" in server["args"]
    env = server["env"]
    assert "SCREENFLOW_CLI" in env
    assert "SCREENFLOW_DB_DIR" in env
    assert "SCREENFLOW_PROFILE" in env
    assert "SCREENFLOW_PROJECT" in env
