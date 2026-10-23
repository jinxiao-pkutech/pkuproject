"""参数校验：每个 MCP 工具的参数先在这里按 04 §2 的 schema 校验，再映射为 CLI 参数。

非法参数抛出 :class:`InvalidInput`，由 server 层转成 ``E_INVALID_INPUT`` 信封，不调用 CLI。
"""

from __future__ import annotations

import re
from typing import Iterable

SCREEN_ID_RE = re.compile(r"^[A-Za-z0-9_]+$")
BREAK_ID_RE = re.compile(r"^b_[0-9a-f]+$")
SYMBOL_ID_RE = re.compile(r"^s_[0-9a-f]+$")
EVIDENCE_ID_RE = re.compile(r"^e_[0-9a-f]+$")

TERMINALS = ("employee", "customer")
LOCALES = ("ja", "en", "ko", "zh-Hans", "zh-Hant", "ja-hira")
DIRECTIONS = ("out", "in")
DETAILS = ("summary", "full")


class InvalidInput(Exception):
    """参数不满足 04 §2 的 schema。"""


def _require(params: dict, key: str) -> object:
    value = params.get(key)
    if value is None or value == "":
        raise InvalidInput(f"缺少必填参数 '{key}'。")
    return value


def _text(params: dict, key: str) -> str:
    value = _require(params, key)
    if not isinstance(value, str):
        raise InvalidInput(f"参数 '{key}' 必须是字符串。")
    return value


def _optional_text(params: dict, key: str, allowed: Iterable[str] | None = None) -> str | None:
    value = params.get(key)
    if value is None or value == "":
        return None
    if not isinstance(value, str):
        raise InvalidInput(f"参数 '{key}' 必须是字符串。")
    if allowed is not None and value not in allowed:
        raise InvalidInput(f"参数 '{key}' 取值非法：{value}。")
    return value


def _optional_int(params: dict, key: str, minimum: int, maximum: int) -> int | None:
    value = params.get(key)
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int):
        raise InvalidInput(f"参数 '{key}' 必须是整数。")
    if value < minimum or value > maximum:
        raise InvalidInput(f"参数 '{key}' 必须在 {minimum}–{maximum} 之间。")
    return value


def _optional_bool(params: dict, key: str) -> bool | None:
    value = params.get(key)
    if value is None:
        return None
    if not isinstance(value, bool):
        raise InvalidInput(f"参数 '{key}' 必须是布尔值。")
    return value


def _screen_id(params: dict) -> str:
    value = _text(params, "screen_id")
    if not SCREEN_ID_RE.fullmatch(value):
        raise InvalidInput(f"画面 ID 格式非法：{value}。")
    return value


def _terminal(params: dict) -> str | None:
    return _optional_text(params, "terminal", TERMINALS)


def _locale(params: dict) -> str | None:
    return _optional_text(params, "locale", LOCALES)


def _detail(params: dict) -> str | None:
    return _optional_text(params, "detail", DETAILS)


def _with_terminal(args: list[str], terminal: str | None) -> list[str]:
    return args + (["--terminal", terminal] if terminal else [])


def index_status(params: dict) -> list[str]:
    profile = _optional_text(params, "profile")
    args = ["index", "status"]
    return args + (["--profile", profile] if profile else [])


def screen_get(params: dict) -> list[str]:
    return _with_terminal(["screen", "get", "--id", _screen_id(params)], _terminal(params))


def screen_controls(params: dict) -> list[str]:
    screen_id = _screen_id(params)
    terminal = _terminal(params)
    locale = _locale(params)
    args = _with_terminal(["screen", "controls", "--id", screen_id], terminal)
    return args + (["--locale", locale] if locale else [])


def screen_constraints(params: dict) -> list[str]:
    screen_id = _screen_id(params)
    terminal = _terminal(params)
    locale = _locale(params)
    args = _with_terminal(["screen", "constraints", "--id", screen_id], terminal)
    return args + (["--locale", locale] if locale else [])


def screen_transitions(params: dict) -> list[str]:
    screen_id = _screen_id(params)
    direction = _text(params, "direction")
    if direction not in DIRECTIONS:
        raise InvalidInput(f"参数 'direction' 取值非法：{direction}。")
    limit = _optional_int(params, "limit", 1, 200)
    args = _with_terminal(["screen", "transitions", "--id", screen_id, "--direction", direction], _terminal(params))
    return args + (["--limit", str(limit)] if limit is not None else [])


def path_find(params: dict) -> list[str]:
    from_screen = _text(params, "from_screen")
    to_screen = _text(params, "to_screen")
    if not SCREEN_ID_RE.fullmatch(from_screen) or not SCREEN_ID_RE.fullmatch(to_screen):
        raise InvalidInput("画面 ID 格式非法。")

    terminal_from = _optional_text(params, "terminal_from", TERMINALS)
    terminal_to = _optional_text(params, "terminal_to", TERMINALS)
    k = _optional_int(params, "k", 1, 10)
    max_hops = _optional_int(params, "max_hops", 1, 40)
    include_prefix = _optional_bool(params, "include_prefix")
    detail = _detail(params)

    args = ["path", "find", "--from", from_screen, "--to", to_screen]
    if terminal_from:
        args += ["--terminal-from", terminal_from]
    if terminal_to:
        args += ["--terminal-to", terminal_to]
    if k is not None:
        args += ["--k", str(k)]
    if max_hops is not None:
        args += ["--max-hops", str(max_hops)]
    if include_prefix:
        args += ["--include-prefix"]
    if detail:
        args += ["--detail", detail]
    return args


def path_prefix(params: dict) -> list[str]:
    to_screen = _text(params, "to_screen")
    if not SCREEN_ID_RE.fullmatch(to_screen):
        raise InvalidInput("画面 ID 格式非法。")
    terminal = _terminal(params)
    detail = _detail(params)
    args = _with_terminal(["path", "prefix", "--to", to_screen], terminal)
    return args + (["--detail", detail] if detail else [])


def break_list(params: dict) -> list[str]:
    category = _optional_text(params, "category")
    screen_id = _optional_text(params, "screen_id")
    edge_id = _optional_text(params, "edge_id")
    limit = _optional_int(params, "limit", 1, 200)
    args = ["break", "list"]
    if category:
        args += ["--category", category]
    if screen_id:
        if not SCREEN_ID_RE.fullmatch(screen_id):
            raise InvalidInput("画面 ID 格式非法。")
        args += ["--screen", screen_id]
    if edge_id:
        args += ["--edge", edge_id]
    if limit is not None:
        args += ["--limit", str(limit)]
    return args


def break_get(params: dict) -> list[str]:
    break_id = _text(params, "break_id")
    if not BREAK_ID_RE.fullmatch(break_id):
        raise InvalidInput(f"断链 ID 格式非法：{break_id}。")
    return ["break", "get", "--id", break_id]


def symbol_find(params: dict) -> list[str]:
    name = _text(params, "name")
    kind = _optional_text(params, "kind")
    limit = _optional_int(params, "limit", 1, 200)
    args = ["symbol", "find", "--name", name]
    if kind:
        args += ["--kind", kind]
    if limit is not None:
        args += ["--limit", str(limit)]
    return args


def symbol_callers(params: dict) -> list[str]:
    symbol_id = _text(params, "symbol_id")
    if not SYMBOL_ID_RE.fullmatch(symbol_id):
        raise InvalidInput(f"符号 ID 格式非法：{symbol_id}。")
    return ["symbol", "callers", "--id", symbol_id]


def symbol_callees(params: dict) -> list[str]:
    symbol_id = _text(params, "symbol_id")
    if not SYMBOL_ID_RE.fullmatch(symbol_id):
        raise InvalidInput(f"符号 ID 格式非法：{symbol_id}。")
    return ["symbol", "callees", "--id", symbol_id]


def evidence_get(params: dict) -> list[str]:
    evidence_id = _text(params, "evidence_id")
    if not EVIDENCE_ID_RE.fullmatch(evidence_id):
        raise InvalidInput(f"证据 ID 格式非法：{evidence_id}。")
    context = _optional_int(params, "context", 0, 10)
    args = ["evidence", "get", "--id", evidence_id]
    return args + (["--context", str(context)] if context is not None else [])


def text_get(params: dict) -> list[str]:
    key = _text(params, "key")
    collection = _optional_text(params, "collection")
    locale = _locale(params)
    args = ["text", "get", "--key", key]
    if collection:
        args += ["--collection", collection]
    if locale:
        args += ["--locale", locale]
    return args


def message_get(params: dict) -> list[str]:
    code = _text(params, "code")
    return ["message", "get", "--code", code]
