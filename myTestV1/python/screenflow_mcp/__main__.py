"""入口：``python -m screenflow_mcp`` 启动 stdio 服务，``--selftest`` 运行自检。"""

from __future__ import annotations

import argparse

from . import server


def main() -> None:
    parser = argparse.ArgumentParser(prog="screenflow_mcp")
    parser.add_argument("--selftest", action="store_true", help="运行 04 §7 的自检并退出。")
    parser.add_argument("--from", dest="from_screen", default="SSAW0903")
    parser.add_argument("--to", dest="to_screen", default="SSAW0907")
    args = parser.parse_args()

    if args.selftest:
        raise SystemExit(server.run_selftest(args.from_screen, args.to_screen))

    server.mcp.run()


if __name__ == "__main__":
    main()
