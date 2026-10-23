# ScreenFlow 使用说明书

> 版本：2026-10-23（复核于全部任务完成后）｜对应代码状态：**T00–T23 全部 DONE**
> 实测：C# 193 passed、Python 35 passed、MiniPos 验收 A-01…A-17 全 PASS（A-02/A-18/A-19 需真实工程，跳过）
> 规格依据：`docs/01`–`docs/06`。本文件是说明书，规格冲突时以 `docs/` 为准。

---

## 第一部分　完整版评估

### 1.1 完整版由什么组成

| 层 | 产物 | 作用 | 状态 |
|---|---|---|---|
| ① 抽取 | `ScreenFlow.CSharp`（Roslyn） | 读 Unity 生成的 `.rsp`，编译 9,400 个 C# 文件，抽出符号、调用点、赋值、对象创建、CFG 可达性、条件、变更标记 | **已完成** |
| ② 索引 | `ScreenFlow.Core` + SQLite（26 张表） | IR 落库、稳定 ID、解析服务（值解析 / 字段指向 / 虚调用分派）、指纹与陈旧检测 | **已完成** |
| ③ 领域 | `ScreenFlow.UnityPos`（15 个适配器） | 把通用事实翻译成 POS 事实：画面、控件与按钮文字、输入约束、TCP 收发、键序、DI、设备/API/原生/SQL/队列/反射边界、入口 | **已完成** |
| ④ 图与路径 | `Core/Graph`（P8）+ `Core/Pathing` | 画面操作图、断链记录（9 类别/13 原因）、K 最短路径、部分路径、诊断 | **已完成** |
| ⑤ 命令面 | `ScreenFlow.Cli` 的全部命令 + `Core/Query`（7 类） | 02 §11.2 的全部命令、输出信封、错误码/退出码、证据片段读取 | **已完成** |
| ⑥ MCP | `python/screenflow_mcp` | 只做调用层：参数校验 → 子进程调 CLI → 原样返回信封 | **已完成** |
| ⑦ 部署 | `deploy/`（agent.md / SKILL.md / mcp.json / README） | VS Code Copilot 自定义 Agent 的三个文件 | **已完成** |
| ⑧ 验收 | `acceptance/` | 在真实源码上跑 A-01…A-19 并核对报告 | **已完成（待在真实源码上执行）** |

**结论：八层全部交付，228 个自动化测试（C# 193 + Python 35）护着。剩下的唯一一件事是在真实 POS 源码上执行 `acceptance/run_acceptance.py` 并按结果调 `config/unitypos.json` 的锚点。**

### 1.2 现在能做什么、不能做什么

**终端**：全部命令可用 —— `index build/status/dump`、`screen get/controls/constraints/transitions`、`path find/prefix`、`break list/get`、`symbol find/get/callers/callees`、`evidence get`、`text get`、`message get`、`debug stats/callsites/sends`。

**MCP / Copilot**：`python -m screenflow_mcp` 可启动；`--selftest` 已在真实 CLI + 夹具索引上 4/4 通过。三个部署文件在 `deploy/` 下，按 `deploy/README.md` 复制即可。

**唯一的"还没做"**：在真实 9,400 文件工程上跑一遍。所有能力都验证过，但验证的对象是 66 文件的仿造夹具。

### 1.3 能力边界（设计上的，不是待办）

- **只做静态解析。** 不运行 POS、不读日志、不猜。注释内容、被注释掉的代码一律不作为事实（设计书明令禁止）。
- **候选不会被提升为确定。** 一个调用点有 3 个可能的实现，就给 3 个候选，不挑"最可能"的那一个。报告里 `candidate` 必须写成"候选"。
- **断链是结果，不是失败。** 跨 TCP、跨设备、跨 API、跨数据库、经反射、经 DI 多实现、延迟执行、键序依赖运行时 Stage —— 这些地方静态分析本来就断，工具的价值在于**精确指出断在哪一行、为什么、以及可能从哪里接上**，而不是假装连得上。
- **画面 ID 同时存在于两个终端时不报错**，两个终端都算，结果里注明终端。

### 1.4 风险评估（交付前必须知道）

| 风险 | 说明 | 处置 |
|---|---|---|
| 真实源码从未验证 | 全部 228 个测试与 MiniPos 验收都跑在 `tests/fixtures/MiniPos/`（按 `docs/03` 仿造的 66 文件迷你工程）。真实工程 9,400 文件从未编译过 | `acceptance/run_acceptance.py --expect acceptance/expectations.pos.json` 就是为此准备；第一次在本机跑必然要修若干锚点配置 |
| 17 项待本机确认 | `docs/PROGRESS.md` 的 Q-01…Q-17：rsp 位置与格式、员工面显示事件、字符串表集合名、队列/API/SQL 的真实类型名等 | 全部写在 `config/unitypos.json` 里，改配置即可，不用改代码 |
| 确定性偶发 | `Symbols_Ids_StableAcrossRuns` 曾偶发失败。真因之一（partial 方法两个符号部件）已修 | 现已有第二道闸：E-35 `Determinism_TwoBuilds_ProduceIdenticalDump`（两次构建导出 SHA-256 对拍）与验收项 A-10，均通过。仍建议在真实工程上连续构建 3 次对拍 |
| `derived` profile | 拿不到 Unity 生成的 rsp 时会退化为自行推导编译输入，精度下降 | 这种索引的 `meta.derivation = "derived"`，Agent 必须在报告开头醒目提示 |
| 索引陈旧 | 源文件改了但没重建索引 | 每次查询都重算指纹，不一致则 `meta.stale = true` |

### 1.5 规模预期

真实工程约 9,400 个 C# 文件，`docs/03` 记录了 125 个重名简单类型名（所以全系统一律按符号 ID 匹配，禁止按短类名）。索引构建是分钟级的一次性操作，由人在终端执行，**不暴露给 Agent**；之后的每次查询都是毫秒级的 SQLite 读。

---

## 第二部分　在 MCP 中如何运作

### 2.1 调用链

```text
测试人员（VS Code / GitHub Copilot Chat）
   │  @screen-flow SSAW0903 SSAW0907
   ▼
Copilot 自定义 Agent        .github/agents/screen-flow.agent.md
   │  只允许调用 screenflow/* 工具；不读源码、不开终端
   ▼
Python MCP 服务（stdio）     python -m screenflow_mcp
   │  subprocess（参数用列表传，禁止 shell 拼接）
   ▼
ScreenFlow CLI              screenflow.exe <group> <command> --db ...
   │
   ▼
SQLite 索引                 .screenflow/<profile>.db
```

关键点：**MCP 服务里没有任何解析逻辑**。它不读数据库、不读源码，只做三件事 —— 校验参数、拼 CLI 命令行、把 CLI 的 JSON 信封原样返回。所以分析结果的对错只跟 CLI 有关，与 MCP 层无关。

### 2.2 环境变量（MCP 服务的全部配置）

| 变量 | 含义 | 示例 |
|---|---|---|
| `SCREENFLOW_CLI` | CLI 可执行文件；以 `.dll` 结尾时自动前置 `dotnet` | `.../publish/screenflow.exe` |
| `SCREENFLOW_DB_DIR` | 索引目录，服务自动追加 `/<profile>.db` | `.../sej_client_pos_unity/.screenflow` |
| `SCREENFLOW_PROFILE` | 默认 profile | `android-onsite` |
| `SCREENFLOW_PROJECT` | Unity 工程根；`evidence get` 读源码片段时追加 `--project` | `.../sej_client_pos_unity` |
| `SCREENFLOW_TIMEOUT_SEC` | 子进程超时，默认 120 | `120` |

### 2.3 `.vscode/mcp.json`（由用户复制到工作区）

```json
{
  "servers": {
    "screenflow": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "screenflow_mcp"],
      "env": {
        "SCREENFLOW_CLI": "${workspaceFolder}/.../myTestV1/publish/screenflow.exe",
        "SCREENFLOW_DB_DIR": "${workspaceFolder}/sej_client_pos_unity/.screenflow",
        "SCREENFLOW_PROFILE": "android-onsite",
        "SCREENFLOW_PROJECT": "${workspaceFolder}/sej_client_pos_unity",
        "PYTHONPATH": "${workspaceFolder}/.../myTestV1/python"
      }
    }
  }
}
```

### 2.4 MCP 工具清单（服务名 `screenflow`）

每个工具都是一条 CLI 命令的薄封装，返回同一个信封 `{ok, data|error, meta}`。

| 工具 | 主要参数 | 对应 CLI |
|---|---|---|
| `sf_index_status` | `profile?` | `index status` |
| `sf_screen_get` | `screen_id`、`terminal?` | `screen get` |
| `sf_screen_controls` | `screen_id`、`terminal?`、`locale?` | `screen controls` |
| `sf_screen_constraints` | `screen_id`、`terminal?`、`locale?` | `screen constraints` |
| `sf_screen_transitions` | `screen_id`、`direction: out\|in`、`limit?` | `screen transitions` |
| `sf_path_find` | `from_screen`、`to_screen`、`k?`(1–10)、`max_hops?`、`include_prefix?`、`detail?` | `path find` |
| `sf_path_prefix` | `to_screen`、`terminal?`、`detail?` | `path prefix` |
| `sf_break_list` / `sf_break_get` | `category?`/`screen_id?`/`edge_id?`　‖　`break_id` | `break list` / `break get` |
| `sf_symbol_find` / `sf_symbol_callers` / `sf_symbol_callees` | `name`　‖　`symbol_id` | `symbol …` |
| `sf_evidence_get` | `evidence_id`、`context?`(0–10) | `evidence get` |
| `sf_text_get` / `sf_message_get` | `key`/`collection?`/`locale?`　‖　`code`（`ERRB022` 或 `22`） | `text get` / `message get` |

三条硬规则：

1. **参数先按 schema 校验**，非法就直接返回 `E_INVALID_INPUT`，**不调用 CLI**。
2. **返回体超过 60,000 字符** → 截断 `data` 中最长的数组（从末尾删元素，绝不截断字符串中间），并置 `meta.truncatedByMcp = true`。
3. CLI 退出码非 0 但 stdout 是合法信封 → 原样返回；stdout 非法 → 返回 `E_CLI_FAILURE`，message 是 stderr 末尾 2,000 字符。

### 2.5 Agent 的固定调用顺序

```text
1. sf_index_status()                     ← derivation=derived 或 stale=true 必须在报告开头提示
2. sf_screen_get(A)、sf_screen_get(Z)    ← 不存在就报告"画面不存在"并结束
3. sf_path_find(A, Z, k=5, include_prefix=true, detail="summary")
4. 对 rank ≤ 3 的路径再来一次 detail="full"（一次调用取回 3 条完整链）
5. 路径上的断链缺字段时 sf_break_get(break_id)，每条最多一次
6. 路径经过的画面去重后最多 8 个，各调 sf_screen_constraints
7. 没有完整路径时用 partialPaths + diagnosis，必要时 sf_screen_transitions(Z, "in")
8. 按 04 §4 的 7 个固定章节生成 Markdown 报告
```

调用次数上限 25 次；达到上限仍未完成，输出已得到的部分并说明。

### 2.6 Agent 必须遵守的报告规则（04 §3.2）

1. 事实只来自工具结果；工具没返回的就写"索引中无此信息"。
2. 不读源码文件、不按命名习惯推测调用关系。
3. **确认度原样转述**：`certain`→确定，`candidate`→候选，`unresolved`→未解析。
4. 任何截断（`meta.truncated`、`truncatedBy`、`edgesTruncated`、`truncatedByMcp`）都要列进"未解析与截断事项"，并且**不得声称"只有这些路径"**。
5. 索引是 `derived` 或 `stale` 时在报告开头醒目提示。
6. 断链逐条列出：类别、断点（文件:行 + 命名空间.类.方法）、契约、原因（用工具返回的 `why` 原文）、回流候选。

### 2.7 MCP 自检

```bash
python -m screenflow_mcp --selftest --from SSAW0903 --to SSAW0907
```

不启动 stdio 服务，依次调 `sf_index_status`、`sf_screen_get`(两端)、`sf_path_find`(summary)，打印每步 `ok` 与耗时，任一失败退出码 1。**接入 Copilot 之前先跑这个**，能把"环境变量写错"和"Agent 配置写错"区分开。

---

## 第三部分　在终端手动运行

### 3.1 前置

- .NET SDK 8（已装：8.0.426）
- Python 3.11+（MCP 层才需要）
- Unity 工程根目录（真实分析时），或本仓库自带的 `tests/fixtures/MiniPos`（练手用）

### 3.2 发布 CLI

```bash
dotnet publish src/ScreenFlow.Cli -c Release -r win-x64 --self-contained false -o publish
```

得到 `publish/screenflow.exe`（配置文件会一起复制到 `publish/config/`）。
开发期也可以不发布，直接用：

```bash
dotnet run --project src/ScreenFlow.Cli -c Release -- <group> <command> [options]
```

> 下文命令统一写作 `screenflow`，等价于上面那条 `dotnet run -- …`。

### 3.3 通用选项（02 §11.1）

| 选项 | 说明 |
|---|---|
| `--project <path>` | Unity 工程根。`index build` 必需；查询时给了就会重算指纹做陈旧检测 |
| `--profile <id>` | 默认 `android-onsite`；本仓库另有 `fixture` 等 4 个 |
| `--db <path>` | 索引文件。默认 `<工程根>/.screenflow/<profile>.db` |
| `--config-dir <path>` | 配置目录（找 `profiles.json` / `unitypos.json`） |
| `--locale <ja\|en\|ko\|zh-Hans\|zh-Hant\|ja-hira>` | 取文字时的语言，默认 `ja` |
| `--pretty` | 缩进输出 |
| `--parallel N` | 抽取并行度 |

**输出约定**：stdout 永远**只有一个 JSON 文档**（信封），进度和日志全走 stderr —— 所以 `screenflow … | jq` 永远安全。

### 3.4 第一步：构建索引

```bash
# 真实工程
screenflow index build --project D:/repo/sej_client_pos_unity --profile android-onsite

# 本仓库夹具（练手）
screenflow index build --project tests/fixtures/MiniPos --profile fixture --db .screenflow/fixture.db
```

成功时 `data.counts` 给出每张表的行数、`data.diagnostics` 给出诊断按代码计数、`data.phases` 列出跑过的阶段（`P1..P9`）。夹具上目前是：

```
files 66, symbols 803, callsites 263, screens 8, controls 22, texts 14, messages 28,
entries 14, boundaries 7, domain_edges 45, screen_edges 17, breaks 17
```

### 3.5 第二步：确认索引可用

```bash
screenflow index status --project tests/fixtures/MiniPos --profile fixture --db .screenflow/fixture.db --pretty
```

看三件事：

- `meta.derivation` —— `native` 是拿到了 Unity 的 rsp；`derived` 表示自行推导，精度下降。
- `data.stale` —— `true` 说明源文件或资源改过，索引该重建了（`data.staleReasons` 会说是哪一类变了）。
- `data.counts` —— 行数为 0 的领域表意味着对应适配器没有命中任何锚点，通常是 `config/unitypos.json` 里的名字和真实工程不一致。

### 3.6 第三步：查询（T20 完成后可用）

以下命令全部可用（`data` 结构按 02 §11）。注意 CLI 的 `--db` 在不给 `--project` 时不会自动推导，建议显式给出。

```bash
# 画面
screenflow screen get         --id SSAW0903
screenflow screen controls    --id SSAW0903 --locale ja
screenflow screen constraints --id SSAW0907
screenflow screen transitions --id SSAW0907 --direction in --limit 50

# 路径（核心用法）
screenflow path find --from SSAW0903 --to SSAW0907 --k 5 --include-prefix --detail full --pretty
screenflow path prefix --to SSAW0903

# 断链
screenflow break list --screen SSAW0903
screenflow break get  --id b_xxxxxxxxxxxxxxxxxxxx

# 符号与证据
screenflow symbol find    --name ExeKeyProc
screenflow symbol callers --id s_xxxxxxxxxxxxxxxxxxxx
screenflow evidence get   --id e_xxxxxxxxxxxxxxxxxxxx --context 3 --project D:/repo/sej_client_pos_unity

# 文字与错误文案
screenflow text get    --key SSAW0903_12 --locale ja
screenflow message get --code ERRB022

# 调试/验收
screenflow debug stats
screenflow debug callsites --file Assets/.../Payment_SSAW0089_Presenter.cs --from-line 100 --to-line 200
screenflow debug sends --target-screen SSAW0907

# 确定性对拍
screenflow index dump --out dump1.txt
```

`path find` 的返回要点：

```json
{ "paths": [ { "rank": 1, "cost": 4.25, "reachedTarget": true,
               "overallConfidence": "candidate",
               "hops": [ { "order": 1, "from": {…}, "to": {…},
                           "edges": [ { "edgeId": "se_…", "trigger": {…},
                                        "triggerText": { "plain": "その他の金額を入力\n(1,000円単位)" },
                                        "conditions": [...], "breakIds": [...], "chain": [...] } ],
                           "edgesTruncated": false } ],
               "breaks": [ … ] } ],
  "partialPaths": [ … ],
  "diagnosis": { "targetHasInboundEdges": true, "fromHasOutboundEdges": true, "note": null } }
```

一条路径由若干跳组成；**一跳可以有多条平行边**（同样两个画面之间存在多条不同的代码链），每条边带自己的条件、断链和完整处理链。`reachedTarget=false` 的在 `partialPaths` 里，止于悬空边 —— 那正是"走到这里就断了，断在哪、为什么"。

### 3.7 退出码与错误码

| 退出码 | 含义 | 典型错误码 |
|---|---|---|
| 0 | 成功 | — |
| 2 | 输入错误 | `E_INVALID_INPUT`、`E_PROFILE_NOT_FOUND`、`E_AMBIGUOUS` |
| 3 | 未找到 | `E_NOT_FOUND`、`E_INDEX_NOT_FOUND` |
| 4 | 构建失败 | `E_NO_COMPILATION_INPUT`、`E_IR_SCHEMA`、`E_ID_COLLISION` |
| 5 | 内部错误 | `E_INTERNAL` |

失败时的信封：

```json
{ "ok": false, "error": { "code": "E_INDEX_NOT_FOUND", "message": "…" }, "meta": { … } }
```

### 3.8 常见问题

| 现象 | 原因与处置 |
|---|---|
| `E_INDEX_NOT_FOUND` | 还没 `index build`，或 `--db` 指错了。索引默认在 `<工程根>/.screenflow/<profile>.db` |
| `E_NO_COMPILATION_INPUT` | 找不到 Unity 生成的 `.rsp`。要先在 Unity 里打开过工程（Android Player 的 rsp 需要做一次 Player 构建） |
| `meta.derivation = "derived"` | 同上但已退化推导。结果可用，精度下降，报告里必须声明 |
| `data.stale = true` | 源码或资源改了。重跑 `index build` |
| 某个领域表行数为 0 | `config/unitypos.json` 的锚点名与真实工程不符。改配置，不要改代码 |
| 中文/日文乱码 | CLI 固定输出 UTF-8。Windows 终端先 `chcp 65001` |
| 找不到 `config/` | 用 `--config-dir` 显式指定 |

### 3.9 开发者命令

```bash
dotnet build ScreenFlow.sln -c Release
dotnet test  ScreenFlow.sln -c Release                      # 当前 193 passed
dotnet test  ScreenFlow.sln -c Release --filter "Task=T19"   # 按任务筛选
python -m pytest python/tests -q                             # 当前 35 passed
```

---

## 附：两条路线的分工

| | MCP（Copilot Agent） | 终端手动 |
|---|---|---|
| 使用者 | 测试人员，不懂代码也能用 | 开发者 / 维护者 |
| 输入 | `@screen-flow SSAW0903 SSAW0907` | 一条条命令 + `jq` |
| 输出 | 7 章节 Markdown 报告 | 原始 JSON 信封 |
| 索引构建 | **不开放**（耗时长、需 Unity 编译输入） | 必须在这里做 |
| 适合 | 日常问"A 到 Z 怎么走、哪里断链" | 排查索引本身、验收、确定性对拍、调试锚点配置 |

---

## 附二：第一次在真实工程上落地的顺序

```bash
# 1. 发布 CLI
dotnet publish src/ScreenFlow.Cli -c Release -r win-x64 --self-contained false -o publish

# 2. 建索引（第一次会暴露锚点配置与真实工程不符的地方）
publish/screenflow.exe index build --project D:/repo/sej_client_pos_unity --profile android-onsite

# 3. 看哪些领域表是 0 行 —— 那就是要改 config/unitypos.json 的地方
publish/screenflow.exe index status --project D:/repo/sej_client_pos_unity --pretty

# 4. 跑验收，对照 docs/03 的事实
python acceptance/run_acceptance.py --project D:/repo/sej_client_pos_unity     --cli publish/screenflow.exe --expect acceptance/expectations.pos.json

# 5. 装 MCP 并自检
pip install -e python
python -m screenflow_mcp --selftest --from SSAW0903 --to SSAW0907

# 6. 复制三个部署文件到工作区，在 Copilot 里选 screen-flow，输入：SSAW0903 SSAW0907
```

**注意**：`SCREENFLOW_CLI` 指向 dll 时文件名是 `screenflow.dll`（程序集名按 02 §11 固定为 `screenflow`），不是 `ScreenFlow.Cli.dll` —— 写错会得到 `E_CLI_FAILURE: 找不到指定的命令或文件`。
