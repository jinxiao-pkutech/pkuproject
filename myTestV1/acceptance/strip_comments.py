"""C# 去注释：删除 ``//`` 行注释与 ``/* */`` 块注释，同时跳过字符串/字符字面量。

字符串、字符与注释的内容一律替换为空格（保留换行），这样既不会把字符串里的
``//`` 误当注释，也不会把字符串里的 ``SslSend(`` 误当调用，同时行号保持不变。
"""

from __future__ import annotations


def strip_comments(source: str) -> str:
    chars = list(source)
    out = list(chars)

    i = 0
    n = len(chars)

    while i < n:
        c = chars[i]
        nxt = chars[i + 1] if i + 1 < n else ""

        if c == "/" and nxt == "/":
            # 行注释到行尾。
            out[i] = " "
            out[i + 1] = " "
            i += 2
            while i < n and chars[i] not in "\r\n":
                out[i] = " "
                i += 1
            continue

        if c == "/" and nxt == "*":
            # 块注释（保留换行）。
            out[i] = " "
            out[i + 1] = " "
            i += 2
            while i < n:
                if chars[i] == "*" and i + 1 < n and chars[i + 1] == "/":
                    out[i] = " "
                    out[i + 1] = " "
                    i += 2
                    break
                if chars[i] not in "\r\n":
                    out[i] = " "
                i += 1
            continue

        if c == "@" and nxt == '"':
            i = _blank_verbatim(chars, out, i)
            continue

        if c == "$":
            if nxt == '"':
                i = _blank_interpolated(chars, out, i)
                continue
            if nxt == "@":
                j = i + 2
                if j < n and chars[j] == '"':
                    i = _blank_verbatim(chars, out, j)
                    continue

        if c == '"':
            i = _blank_string(chars, out, i)
            continue

        if c == "'":
            i = _blank_char(chars, out, i)
            continue

        i += 1

    return "".join(out)


def _blank_string(chars: list[str], out: list[str], start: int) -> int:
    return _blank_until_quote(chars, out, start, '"')


def _blank_char(chars: list[str], out: list[str], start: int) -> int:
    return _blank_until_quote(chars, out, start, "'")


def _blank_until_quote(chars: list[str], out: list[str], start: int, quote: str) -> int:
    """从开引号到下一个未转义引号之间的内容置为空格。返回下一个要处理的下标。"""
    out[start] = " "
    i = start + 1
    n = len(chars)

    while i < n:
        c = chars[i]
        if c == "\\" and i + 1 < n:
            out[i] = " "
            out[i + 1] = " "
            i += 2
            continue
        if c == quote:
            out[i] = " "
            return i + 1
        if c not in "\r\n":
            out[i] = " "
        i += 1

    return i


def _blank_verbatim(chars: list[str], out: list[str], start: int) -> int:
    """逐字字符串 ``@"…"``：``""`` 是转义引号。start 指向开引号。"""
    out[start] = " "
    i = start + 1
    n = len(chars)

    while i < n:
        c = chars[i]
        if c == '"':
            if i + 1 < n and chars[i + 1] == '"':
                out[i] = " "
                out[i + 1] = " "
                i += 2
                continue
            out[i] = " "
            return i + 1
        if c not in "\r\n":
            out[i] = " "
        i += 1

    return i


def _blank_interpolated(chars: list[str], out: list[str], start: int) -> int:
    """插值字符串 ``$"…"``：跳过表达式里的嵌套大括号，只置空字符串段。"""
    out[start] = " "
    out[start + 1] = " "
    i = start + 2
    n = len(chars)
    depth = 0

    while i < n:
        c = chars[i]
        if c == "\\" and i + 1 < n:
            out[i] = " "
            out[i + 1] = " "
            i += 2
            continue
        if c == "{":
            depth += 1
            if i + 1 < n and chars[i + 1] == "{":
                out[i] = " "
                out[i + 1] = " "
                i += 2
                depth -= 1
                continue
            # 表达式内部交给普通扫描处理（这里不动字符，只推进并平衡大括号）。
            i += 1
            while i < n and depth > 0:
                if chars[i] == "{":
                    depth += 1
                elif chars[i] == "}":
                    depth -= 1
                i += 1
            continue
        if c == '"' and depth == 0:
            out[i] = " "
            return i + 1
        if c not in "\r\n":
            out[i] = " "
        i += 1

    return i
