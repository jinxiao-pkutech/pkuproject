# 04 运行时 Agent 设计书（Copilot 自定义 Agent + Python MCP）

## 1. 运行结构

```text
测试人员（VS Code GitHub Copilot Chat）
   │  @screen-flow SSAW0903 SSAW0907
   ▼
Copilot 自定义 Agent  .github/agents/screen-flow.agent.md
   │  只允许调用 screenflow MCP 工具（不读源码、不用终端）
   ▼
Python MCP 服务（stdio）  python -m screenflow_mcp
   │  子进程
   ▼
ScreenFlow CLI  →  SQLite 索引（.screenflow/<profile>.db）
```

- 索引构建（`index build`）由人在终端执行，**不暴露给 Agent**（耗时长、需要 Unity 编译输入）。
- Agent 的全部事实都来自 MCP 工具返回值。

## 2. MCP 工具清单

服务名：`screenflow`。每个工具都把参数映射为 CLI 参数，返回 CLI 信封（`{ok, data|error, meta}`）。

| 工具 | 参数（JSON Schema 要点） | 对应 CLI |
|---|---|---|
| `sf_index_status` | `profile?: string` | `index status` |
| `sf_screen_get` | `screen_id: string`（必填，`^[A-Za-z0-9_]+$`）、`terminal?: "employee"\|"customer"` | `screen get` |
| `sf_screen_controls` | `screen_id`、`terminal?`、`locale?: "ja"\|"en"\|"ko"\|"zh-Hans"\|"zh-Hant"\|"ja-hira"` | `screen controls` |
| `sf_screen_constraints` | `screen_id`、`terminal?`、`locale?` | `screen constraints` |
| `sf_screen_transitions` | `screen_id`、`direction: "out"\|"in"`、`terminal?`、`limit?: 1–200` | `screen transitions` |
| `sf_path_find` | `from_screen`、`to_screen`、`terminal_from?`、`terminal_to?`、`k?: 1–10`（默认 5）、`max_hops?: 1–40`、`include_prefix?: bool`（默认 true）、`detail?: "summary"\|"full"`（默认 summary） | `path find` |
| `sf_path_prefix` | `to_screen`、`terminal?`、`detail?` | `path prefix` |
| `sf_break_list` | `category?`、`screen_id?`、`edge_id?`、`limit?: 1–200` | `break list` |
| `sf_break_get` | `break_id: string`（`^b_[0-9a-f]+$`） | `break get` |
| `sf_symbol_find` | `name: string`、`kind?`、`limit?` | `symbol find` |
| `sf_symbol_callers` / `sf_symbol_callees` | `symbol_id: string`（`^s_[0-9a-f]+$`） | `symbol callers/callees` |
| `sf_evidence_get` | `evidence_id: string`（`^e_[0-9a-f]+$`）、`context?: 0–10` | `evidence get` |
| `sf_text_get` | `key: string`、`collection?`、`locale?` | `text get` |
| `sf_message_get` | `code: string`（`ERRB022` 或 `22`） | `message get` |

- 参数先按 schema 校验，非法 → 返回 `{ok:false, error:{code:"E_INVALID_INPUT"}}`，不调用 CLI。
- 每个工具的 `description` 用中文写明：用途、何时调用、返回内容要点、**“返回的 confidence 必须原样转述”**。
- 工具返回体超过 60,000 字符 → 截断 `data` 中最长的数组并设置 `meta.truncatedByMcp = true`（截断数组末尾元素，不截断字符串中间）。

## 3. Copilot 自定义 Agent（`deploy/.github/agents/screen-flow.agent.md`）

### 3.1 frontmatter

```yaml
---
name: screen-flow
description: "输入开始画面ID与结束画面ID，基于 ScreenFlow 静态解析索引，输出两画面间的全部候选路径、每一跳的源码处理链、API/硬件交互，以及全部断链（位置与原因）。"
tools: ['screenflow/*']
argument-hint: "<开始画面ID> <结束画面ID> [terminal_from=employee|customer] [terminal_to=…] [k=5]"
---
```

### 3.2 正文必须包含的规则

1. **事实只来自工具结果。** 画面、类、方法、文件、行号、按钮文字、事件路径、API、错误码，全部原样取自工具返回；工具没有返回的内容写“索引中无此信息”。
2. **不读源码文件，不推测。** 不得根据命名习惯推断调用关系或画面。
3. **确认度原样转述。** `candidate` 写“候选”，`certain` 写“确定”，`unresolved` 写“未解析”；任何 `candidate` 不得写成确定语气。
4. **截断必须告知。** `meta.truncated` / `truncatedBy` / `edgesTruncated` / `meta.truncatedByMcp` 任一为真 → 在报告“未解析与截断事项”中列出，并不得声称“只有这些路径”。
5. **索引状态必须告知。** `derivation = derived` 或 `stale = true` → 报告开头醒目提示。
6. **断链必须逐条列出**，每条给出：类别、断点（文件:行 + 命名空间.类.方法）、契约、原因（使用工具返回的 `why` 原文）、回流候选。
7. 调用次数上限 25 次；达到上限仍未完成 → 输出已得到的部分并说明。

### 3.3 固定调用顺序

```text
1. sf_index_status()
2. sf_screen_get(A)、sf_screen_get(Z)
   - 不存在 → 报告“画面不存在”并结束
   - 存在于两个终端且用户未指定 → 两个终端都计算，报告中分开
3. sf_path_find(A, Z, k=参数或5, include_prefix=true, detail="summary")
4. 对 rank ≤ 3 的路径：sf_path_find(A, Z, k=3, include_prefix=true, detail="full")（一次调用即可取得 3 条路径的完整链）
5. 路径中出现的断链若缺少字段 → sf_break_get(break_id)（每条最多一次）
6. 路径经过的每个画面（去重，最多 8 个）：sf_screen_constraints(画面)；有约束才写入报告
7. 没有完整路径：使用 partialPaths 与 diagnosis；必要时 sf_screen_transitions(Z, "in") 说明终点的入边情况
8. 生成报告（§4）
```

## 4. 报告格式（Markdown，固定章节）

```markdown
# 画面路径与断链报告：{A}（{终端}）→ {Z}（{终端}）

> 索引：profile={profileId} / derivation={native|derived} / stale={true|false} / 构建时间={builtAt}
> （derived 或 stale 时在此加一行警告）

## 1. 结论摘要
| 项目 | 内容 |
|---|---|
| 完整路径数 | N（截断：是/否） |
| 部分路径数 | M |
| 断链数（按类别） | ExternalHardware x / TerminalTcpCurrentScreen y / … |
| 全部确定的路径 | 有/无 |
| 最主要的断点 | 断链 ID + 一句话（取 why 原文的第一句） |

## 2. 前置路径（{待机画面} → {A}）
（表格同第 4 节逐跳格式；无则写“未找到”并给出 partialPaths 的断点）

## 3. 路径一览
| 排名 | 画面序列 | 确认度 | 断链数 | cost |
|---|---|---|---|---|

## 4. 路径详情
### 路径 {rank}：{画面序列}
| 跳 | 终端 | 当前画面 | 触发 | 按钮文字(ja) / 控件ID | 处理链摘要 | API / 硬件 | 条件 | 确认度 |
|---|---|---|---|---|---|---|---|---|

处理链摘要：只列关键步骤（入口方法、socketSend/Receive、keySeqDispatch、remoteDisplay/navigate、deviceCommand、apiCall、enqueue），格式 `文件:行 命名空间.类.方法`。
完整链放在 `<details><summary>完整处理链</summary> … </details>` 中，逐步列出 chain 全部步骤。

## 5. 断链清单
| ID | 类别 | 断点（文件:行 / 命名空间.类.方法） | 契约 | 原因（why 原文） | 回流候选 |
|---|---|---|---|---|---|

## 6. 路径上画面的输入约束
| 画面 | 约束 | 值 | 错误码 | 错误文案(ja / フルセルフ) | 条件 | 所在方法 | 确认度 |
|---|---|---|---|---|---|---|---|

## 7. 未解析与截断事项
- （逐条列出：截断、未解析的文字键、未解析的事件路径、禁用的功能 disabledFeatures 等）
```

## 5. Skill（`deploy/.github/skills/screen-flow/SKILL.md`）

frontmatter：

```yaml
---
name: screen-flow
description: "ScreenFlow MCP 工具的使用方法：查询画面、控件文字、输入约束、画面间路径与断链。需要回答“从画面A到画面B经过哪些代码/画面、哪里断链”时使用。"
---
```

正文：每个工具一节（用途、参数、返回要点、常见错误码与处理）；“确认度与断链类别”术语表（§8.3 的 9 个类别、13 个原因码的中文说明）；§3.2 的规则全文。

## 6. `deploy/.vscode/mcp.json`

```json
{
  "servers": {
    "screenflow": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "screenflow_mcp"],
      "env": {
        "SCREENFLOW_CLI": "${workspaceFolder}/issue-unit-test-preparation-agent_assets/myTestV1/publish/screenflow.exe",
        "SCREENFLOW_DB_DIR": "${workspaceFolder}/sej_client_pos_unity/.screenflow",
        "SCREENFLOW_PROFILE": "android-onsite",
        "SCREENFLOW_PROJECT": "${workspaceFolder}/sej_client_pos_unity",
        "PYTHONPATH": "${workspaceFolder}/issue-unit-test-preparation-agent_assets/myTestV1/python"
      }
    }
  }
}
```

`deploy/README.md` 写明：发布 CLI（`dotnet publish src/ScreenFlow.Cli -c Release -r win-x64 --self-contained false -o publish`）、安装 Python 依赖（`pip install -e python`）、构建索引、复制三个部署文件、在 Copilot 中选择 `screen-flow` Agent 并输入 `SSAW0903 SSAW0907` 验证。

## 7. MCP 自检

`python -m screenflow_mcp --selftest [--from SSAW0903 --to SSAW0907]`：不启动 stdio 服务，依次调用 `sf_index_status`、`sf_screen_get`(from/to)、`sf_path_find`(summary)，打印每步 `ok` 与耗时，任一失败退出码 1。
