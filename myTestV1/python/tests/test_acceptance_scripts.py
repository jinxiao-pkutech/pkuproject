"""T23 验收脚本测试：strip_comments、断言 DSL、报告核对。"""

from __future__ import annotations

import json

from acceptance import check_report, strip_comments
from acceptance.run_acceptance import assert_matches, resolve


def test_strip_comments_handles_strings() -> None:
    source = '''\
var a = "// not a comment";
var b = "http://x";
// real comment
var c = @"verbatim // and /* */";
var d = $"interp {1 + 2}";
var e = '\\''; // char with escaped quote
/* block
   comment */
var f = "end";
'''
    stripped = strip_comments.strip_comments(source)

    assert "// not a comment" not in stripped
    assert "real comment" not in stripped
    assert "block" not in stripped
    # 真正的代码行仍在。
    assert 'var a =' in stripped
    assert 'var f = "end"' not in stripped  # 字符串被置空
    assert "var f =" in stripped


def test_assert_dsl() -> None:
    envelope = {
        "ok": True,
        "data": {
            "paths": [
                {
                    "overallConfidence": "candidate",
                    "hops": [{"edges": [{"edgeId": "se_1"}, {"edgeId": "se_2"}]}],
                }
            ]
        },
        "meta": {"stale": False},
    }

    assert assert_matches(envelope, {"op": "exists", "path": "data.paths"})
    assert assert_matches(envelope, {"op": "equals", "path": "ok", "value": True})
    assert assert_matches(envelope, {"op": "count>=", "path": "data.paths", "value": 1})
    assert assert_matches(envelope, {"op": "count>=", "path": "data.paths[0].hops[0].edges", "value": 2})
    assert assert_matches(
        envelope,
        {"op": "contains", "path": "data.paths[0].hops[0].edges", "where": {"op": "equals", "path": "edgeId", "value": "se_2"}},
    )
    assert assert_matches(envelope, {"op": "allNot", "path": "meta.stale", "value": True})


def test_check_report_detects_unknown_name(tmp_path) -> None:
    report = tmp_path / "report.md"
    report.write_text(
        "## 1. 结论摘要\n"
        "主要断点：`NotARealSymbol`，文件 `Assets/NotARealFile.cs:42`。\n"
        "已知符号 `Payment_SSAW0089_Presenter`。\n",
        encoding="utf-8",
    )

    a06 = tmp_path / "a06.json"
    a06.write_text(
        json.dumps(
            {
                "ok": True,
                "data": {
                    "paths": [
                        {"hops": [{"edges": [{"chain": [{"type": "Payment_SSAW0089_Presenter", "file": "Assets/ScenePosRegister/Payment/Presenter/Script/Payment_SSAW0089_Presenter.cs", "line": 60}]}]}]}
                    ]
                },
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    missing = check_report.check_report(report, a06)
    assert "NotARealSymbol" in missing
    assert "Assets/NotARealFile.cs" in missing
    assert "Payment_SSAW0089_Presenter" not in missing
