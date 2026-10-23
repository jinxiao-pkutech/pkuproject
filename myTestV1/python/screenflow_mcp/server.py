"""MCP 工具注册：把 04 §2 的每个工具映射到 CLI 参数，返回截断后的信封。

所有工具都只做：参数校验 → 子进程调用 → 截断。不读数据库、不读源码。
"""

from __future__ import annotations

import time

from mcp.server.fastmcp import FastMCP

from . import cli_runner, schemas, truncate

mcp = FastMCP("screenflow")


def _invalid(message: str) -> dict:
    return {"ok": False, "error": {"code": "E_INVALID_INPUT", "message": message}, "meta": {}}


def _call(args: list[str]) -> dict:
    return truncate.truncate(cli_runner.run(args))


def _validate(tool: str, params: dict) -> list[str]:
    try:
        return getattr(schemas, tool)(params)
    except schemas.InvalidInput as exc:
        raise _Invalid(str(exc)) from exc


class _Invalid(Exception):
    """参数校验失败，携带 E_INVALID_INPUT 信封。"""

    def __init__(self, message: str):
        super().__init__(message)
        self.message = message


@mcp.tool()
def sf_index_status(profile: str | None = None) -> dict:
    """查询索引状态（meta、stale、各表行数）。用于开始回答前确认索引是否存在、是否陈旧；返回的 confidence 必须原样转述。"""
    try:
        args = _validate("index_status", {"profile": profile})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_screen_get(screen_id: str, terminal: str | None = None) -> dict:
    """按画面 ID 查画面登记信息与 View/Presenter 符号摘要。同一 ID 存在于两个终端且未指定 terminal 时返回数组；返回的 confidence 必须原样转述。"""
    try:
        args = _validate("screen_get", {"screen_id": screen_id, "terminal": terminal})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_screen_controls(screen_id: str, terminal: str | None = None, locale: str | None = None) -> dict:
    """查一个画面的控件、层级、文字键与文字（raw/plain）、事件处理方法与确认度。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("screen_controls", {"screen_id": screen_id, "terminal": terminal, "locale": locale})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_screen_constraints(screen_id: str, terminal: str | None = None, locale: str | None = None) -> dict:
    """查一个画面的输入约束（最大位数、上下限、允许空值等）与对应错误文案、所在方法。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("screen_constraints", {"screen_id": screen_id, "terminal": terminal, "locale": locale})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_screen_transitions(screen_id: str, direction: str, terminal: str | None = None, limit: int | None = None) -> dict:
    """查一个画面的出边（direction=out）或入边（direction=in）摘要。返回的 confidence 必须原样转述。"""
    try:
        args = _validate(
            "screen_transitions",
            {"screen_id": screen_id, "direction": direction, "terminal": terminal, "limit": limit},
        )
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_path_find(
    from_screen: str,
    to_screen: str,
    terminal_from: str | None = None,
    terminal_to: str | None = None,
    k: int | None = 5,
    max_hops: int | None = None,
    include_prefix: bool | None = True,
    detail: str | None = "summary",
) -> dict:
    """找两个画面之间的全部候选路径（Yen K 最短），含每一跳的源码处理链、API/硬件交互与断链；detail=full 时含处理链。返回的 confidence 必须原样转述。"""
    try:
        args = _validate(
            "path_find",
            {
                "from_screen": from_screen,
                "to_screen": to_screen,
                "terminal_from": terminal_from,
                "terminal_to": terminal_to,
                "k": k,
                "max_hops": max_hops,
                "include_prefix": include_prefix,
                "detail": detail,
            },
        )
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_path_prefix(to_screen: str, terminal: str | None = None, detail: str | None = None) -> dict:
    """查从待机画面到指定画面的前置路径。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("path_prefix", {"to_screen": to_screen, "terminal": terminal, "detail": detail})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_break_list(category: str | None = None, screen_id: str | None = None, edge_id: str | None = None, limit: int | None = None) -> dict:
    """列出断链记录，可按类别、画面或边过滤。返回的 confidence 必须原样转述。"""
    try:
        args = _validate(
            "break_list",
            {"category": category, "screen_id": screen_id, "edge_id": edge_id, "limit": limit},
        )
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_break_get(break_id: str) -> dict:
    """按 ID 查一条断链的完整记录（位置、类别、原因、恢复置信度）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("break_get", {"break_id": break_id})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_symbol_find(name: str, kind: str | None = None, limit: int | None = None) -> dict:
    """按简单名查符号候选（同名全部列出，带 fqName/file）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("symbol_find", {"name": name, "kind": kind, "limit": limit})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_symbol_callers(symbol_id: str) -> dict:
    """查一个方法/符号的所有调用者（含确认度）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("symbol_callers", {"symbol_id": symbol_id})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_symbol_callees(symbol_id: str) -> dict:
    """查一个方法/符号体内调用的所有被调方（含确认度）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("symbol_callees", {"symbol_id": symbol_id})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_evidence_get(evidence_id: str, context: int | None = 3) -> dict:
    """按证据 ID 读取源码片段（从工程实时读取并校验 snippet_hash，不一致标 stale）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("evidence_get", {"evidence_id": evidence_id, "context": context})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_text_get(key: str, collection: str | None = None, locale: str | None = None) -> dict:
    """按文字键查本地化文字（raw/plain）。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("text_get", {"key": key, "collection": collection, "locale": locale})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


@mcp.tool()
def sf_message_get(code: str, locale: str | None = None) -> dict:
    """按错误码（如 ERRB022 或 22）查错误文案。返回的 confidence 必须原样转述。"""
    try:
        args = _validate("message_get", {"code": code, "locale": locale})
    except _Invalid as exc:
        return _invalid(exc.message)
    return _call(args)


def run_selftest(from_screen: str = "SSAW0903", to_screen: str = "SSAW0907") -> int:
    """04 §7：依次调用状态、两个画面、一次路径搜索，打印每步 ok 与耗时，任一失败退出码 1。"""
    steps: list[tuple[str, object]] = [
        ("sf_index_status", lambda: sf_index_status()),
        (f"sf_screen_get({from_screen})", lambda: sf_screen_get(from_screen)),
        (f"sf_screen_get({to_screen})", lambda: sf_screen_get(to_screen)),
        (f"sf_path_find({from_screen}->{to_screen})", lambda: sf_path_find(from_screen, to_screen)),
    ]

    all_ok = True
    for name, step in steps:
        started = time.monotonic()
        envelope = step()  # type: ignore[operator]
        elapsed = (time.monotonic() - started) * 1000
        ok = bool(envelope.get("ok")) if isinstance(envelope, dict) else False
        print(f"{name}: ok={ok} elapsedMs={elapsed:.0f}")
        all_ok = all_ok and ok

    return 0 if all_ok else 1
