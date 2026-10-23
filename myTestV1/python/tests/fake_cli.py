#!/usr/bin/env python3
"""Fake ScreenFlow CLI：记录收到的参数，并按 FAKE_CLI_MODE 决定行为。"""

from __future__ import annotations

import json
import os
import sys
import time


def main() -> None:
    args = sys.argv[1:]

    record = os.environ.get("FAKE_CLI_RECORD")
    if record:
        with open(record, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(args, ensure_ascii=False) + "\n")

    mode = os.environ.get("FAKE_CLI_MODE", "ok")

    if mode == "sleep":
        time.sleep(int(os.environ.get("FAKE_CLI_SLEEP", "5")))
        return

    if mode == "invalid-stdout":
        sys.stdout.write("this is not json")
        sys.exit(0)

    if mode == "error-valid":
        print(json.dumps({"ok": False, "error": {"code": "E_NOT_FOUND", "message": "fake not found"}, "meta": {}}))
        sys.exit(3)

    if mode == "error-invalid":
        sys.stdout.write("{broken json")
        sys.stderr.write("boom\n" * 200)
        sys.exit(5)

    if mode == "big":
        payload = {"items": [{"i": i, "text": "x" * 100} for i in range(500)]}
        print(json.dumps({"ok": True, "data": payload, "meta": {"stale": False}}, ensure_ascii=False))
        sys.exit(0)

    print(json.dumps({"ok": True, "data": {"args": args}, "meta": {"stale": False}}, ensure_ascii=False))
    sys.exit(0)


if __name__ == "__main__":
    main()
