"""truncate：超过 60,000 字符时截断 data 中最长数组的末尾元素。"""

from __future__ import annotations

import json

from screenflow_mcp import truncate


def test_short_envelope_is_untouched() -> None:
    envelope = {"ok": True, "data": {"x": 1}, "meta": {"stale": False}}
    result = truncate.truncate(envelope)
    assert result == envelope
    assert "truncatedByMcp" not in result["meta"]


def test_longest_list_is_trimmed_from_the_end() -> None:
    paths = [{"rank": i, "text": "x" * 200} for i in range(400)]
    envelope = {
        "ok": True,
        "data": {
            "paths": paths,
            "breaks": [{"id": i} for i in range(5)],
        },
        "meta": {"stale": False},
    }

    original_count = len(paths)
    assert len(json.dumps(envelope, ensure_ascii=False)) > truncate.MAX_CHARS

    result = truncate.truncate(envelope)
    assert len(json.dumps(result, ensure_ascii=False)) <= truncate.MAX_CHARS
    assert result["meta"]["truncatedByMcp"] is True
    assert result["meta"]["truncatedPath"] == "data.paths"
    # 只能少、不能多：截断的是数组末尾元素，前段保持不变。
    assert len(result["data"]["paths"]) < original_count
    assert result["data"]["paths"][0]["rank"] == 0


def test_truncation_preserves_other_keys() -> None:
    envelope = {
        "ok": True,
        "data": {"items": [{"i": i, "text": "x" * 200} for i in range(400)]},
        "meta": {"stale": False, "profileId": "fixture"},
    }
    result = truncate.truncate(envelope)
    assert result["ok"] is True
    assert result["meta"]["stale"] is False
    assert result["meta"]["profileId"] == "fixture"
    assert result["meta"]["truncatedByMcp"] is True
