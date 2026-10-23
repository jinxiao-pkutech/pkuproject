# 本机验收脚本

在**有真实 POS 源码的机器**上执行 05 §6.2 的 A-01 … A-19；在生成机器上用 MiniPos 期望文件跑通除人工项外的全部项目。

## 文件

```
acceptance/
├── run_acceptance.py            # 逐项执行、断言、交叉核对，生成 results/<时间戳>/
├── check_report.py              # 核对 Copilot 报告里的标识符是否都在 A-06 输出中
├── strip_comments.py            # C# 去注释（跳过字符串/字符/逐字/插值字符串）
├── expectations.pos.json        # 真实 POS 的期望（值取自 03 §13 与 05 §6.2）
├── expectations.minipos.json    # MiniPos 夹具的期望（值取自 05 §4）
└── README.md
```

## 用法

```bash
# 真实 POS（默认 profiles android-onsite,windows-editor）
python acceptance/run_acceptance.py --project <sej_client_pos_unity> --cli publish/screenflow.exe --expect acceptance/expectations.pos.json

# MiniPos 夹具（生成机器回归）
python acceptance/run_acceptance.py --project tests/fixtures/MiniPos --cli publish/screenflow.dll \
  --expect acceptance/expectations.minipos.json --profiles fixture --skip A-02,A-18,A-19
```

结果输出到 `acceptance/results/<时间戳>/`：每项的原始输出 `A-xx.json`、`summary.md`、`summary.json`。任一 FAIL 退出码 1。

## 断言 DSL

期望文件里的断言：

| op | 含义 |
|---|---|
| `exists` | 路径存在 |
| `equals` | 路径值等于给定值 |
| `contains` | 字符串包含；数组含某元素或某元素满足 `where` |
| `count>=` | 数组长度 ≥ 给定值 |
| `allNot` | 所有值都不等于给定值 |
| `and` / `or` / `not` | 组合 |

JSON 路径用点号 + `[*]`，如 `data.paths[0].hops[0].edges`。

## 交叉核对项（A-11 … A-16）

这些项不写 CLI 命令，而是在 `run_acceptance.py` 里实现：对 `Assets/**/*.cs` 去注释后做正则计数，与 `debug stats` / `debug callsites` / `debug sends` 的结果比对；差值逐条列到 summary。
