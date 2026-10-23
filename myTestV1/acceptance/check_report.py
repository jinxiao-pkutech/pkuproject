#!/usr/bin/env python3
"""报告核对：检查报告里出现的标识符 / 文件路径是否都能在 A-06 输出中找到。

用法：
  python acceptance/check_report.py <报告.md> <A-06输出.json>
未找到的标识符逐行打印，有则退出码 1。
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

_BACKTICK = re.compile(r"`([^`\n]+)`")
_FILE_LINE = re.compile(r"([A-Za-z0-9_./\\-]+\.cs):(\d+)")


def extract_markers(markdown: str) -> set[str]:
    markers: set[str] = set()
    markers.update(_BACKTICK.findall(markdown))
    for path, _line in _FILE_LINE.findall(markdown):
        markers.add(path)
    return markers


def check_report(report_path: str | Path, a06_path: str | Path) -> list[str]:
    report = Path(report_path).read_text(encoding="utf-8")
    a06 = json.loads(Path(a06_path).read_text(encoding="utf-8"))

    markers = extract_markers(report)
    haystack = json.dumps(a06, ensure_ascii=False)

    missing = sorted(marker for marker in markers if marker not in haystack)
    return missing


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 2:
        print("用法：python acceptance/check_report.py <报告.md> <A-06输出.json>", file=sys.stderr)
        return 2

    missing = check_report(args[0], args[1])
    for marker in missing:
        print(marker)

    if missing:
        print(f"未找到 {len(missing)} 个标识符。", file=sys.stderr)

    return 1 if missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
