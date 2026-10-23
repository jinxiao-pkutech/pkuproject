"""信封截断：返回体超过 60,000 字符时，截掉 ``data`` 中最长数组的末尾元素。"""

from __future__ import annotations

import json

MAX_CHARS = 60_000


def _serialized_length(envelope: dict) -> int:
    return len(json.dumps(envelope, ensure_ascii=False))


def _longest_list(node: object, path: str, best: list) -> None:
    """在 data 里找元素最多的列表；并列时取先遇到的（遍历顺序确定）。"""
    if isinstance(node, dict):
        for key, value in node.items():
            _longest_list(value, f"{path}.{key}", best)
    elif isinstance(node, list):
        if len(node) > best[0]:
            best[0] = len(node)
            best[1] = node
            best[2] = path
        for index, item in enumerate(node):
            _longest_list(item, f"{path}[{index}]", best)


def truncate(envelope: dict, max_chars: int = MAX_CHARS) -> dict:
    """就地截断信封，设置 ``meta.truncatedByMcp`` 与 ``meta.truncatedPath``。"""
    if not isinstance(envelope, dict):
        return envelope

    if _serialized_length(envelope) <= max_chars:
        return envelope

    # [长度, 列表引用, 路径]
    best: list = [0, None, None]
    data = envelope.get("data")
    if data is not None:
        _longest_list(data, "data", best)

    target = best[1]
    if not isinstance(target, list):
        return envelope

    while _serialized_length(envelope) > max_chars and target:
        target.pop()

    meta = envelope.setdefault("meta", {})
    if not isinstance(meta, dict):
        return envelope
    meta["truncatedByMcp"] = True
    meta["truncatedPath"] = best[2]

    return envelope
