---
name: screen-flow
description: "ScreenFlow MCP 工具的使用方法：查询画面、控件文字、输入约束、画面间路径与断链。需要回答“从画面A到画面B经过哪些代码/画面、哪里断链”时使用。"
---

# ScreenFlow Skill

本 Skill 说明 screenflow MCP 工具如何调用、返回什么、以及如何解读确认度与断链类别。

## 工具一览

| 工具 | 参数 | 对应 CLI | 用途 |
|---|---|---|---|
| `sf_index_status` | `profile?` | `index status` | 查索引 meta、stale、各表行数 |
| `sf_screen_get` | `screen_id`、`terminal?` | `screen get` | 查画面与 View/Presenter 符号 |
| `sf_screen_controls` | `screen_id`、`terminal?`、`locale?` | `screen controls` | 查控件、文字键、文字、处理方法 |
| `sf_screen_constraints` | `screen_id`、`terminal?`、`locale?` | `screen constraints` | 查输入约束与错误文案 |
| `sf_screen_transitions` | `screen_id`、`direction`、`terminal?`、`limit?` | `screen transitions` | 查出边/入边摘要 |
| `sf_path_find` | `from_screen`、`to_screen`、`terminal_from?`、`terminal_to?`、`k?`、`max_hops?`、`include_prefix?`、`detail?` | `path find` | 查画面间候选路径与断链 |
| `sf_path_prefix` | `to_screen`、`terminal?`、`detail?` | `path prefix` | 查待机画面到目标的前置路径 |
| `sf_break_list` | `category?`、`screen_id?`、`edge_id?`、`limit?` | `break list` | 列断链（可过滤） |
| `sf_break_get` | `break_id` | `break get` | 查一条断链完整记录 |
| `sf_symbol_find` | `name`、`kind?`、`limit?` | `symbol find` | 按简单名查符号候选 |
| `sf_symbol_callers` | `symbol_id` | `symbol callers` | 查调用者 |
| `sf_symbol_callees` | `symbol_id` | `symbol callees` | 查被调方 |
| `sf_evidence_get` | `evidence_id`、`context?` | `evidence get` | 读源码片段并校验 snippet_hash |
| `sf_text_get` | `key`、`collection?`、`locale?` | `text get` | 查本地化文字 |
| `sf_message_get` | `code` | `message get` | 查错误文案（ERRB022 或 22） |

## 返回信封

每个工具返回 `{ok, data|error, meta}`。`ok=false` 时看 `error.code`：

- `E_INDEX_NOT_FOUND`：索引不存在，先 `index build`。
- `E_NOT_FOUND`：查询的对象不存在。
- `E_INVALID_INPUT`：参数非法。
- `E_CLI_FAILURE`：CLI 调用失败或超时。
- `E_AMBIGUOUS` / `E_LIMIT_EXCEEDED`：歧义 / 超限。

## 确认度术语

- `certain`：确定。
- `candidate`：候选，**必须写成“候选”，不得写成确定语气**。
- `unresolved`：未解析。

## 断链类别（9 类）

| 类别 | 含义 |
|---|---|
| `ExternalHardware` | 调用硬件命令，硬件侧源码不在本项目，经 MQTT 异步返回。 |
| `ExternalMicroservice` | 调用微服务 API，服务端源码不在本项目。 |
| `TerminalTcpCurrentScreen` | 向对端终端发送事件，接收方取决于运行时画面状态。 |
| `NativeInterop` | 调用 Android 原生代码，实现不在 C# 源码中。 |
| `DatabaseMediated` | 从数据库表读数据，数据由其他处理写入，调用链中断。 |
| `DiAmbiguous` | 从 DI 取对象存在多个注册实现或经反射创建，实际使用取决于运行时。 |
| `DeferredExecution` | 处理被放入队列延迟执行，不在当前调用中同步运行。 |
| `KeySeqStageDependent` | 键序事件的实际处理函数由运行时 Stage 决定。 |
| `Unknown` | 无法绑定、无法静态确定、超限截断、或画面未登记。 |

## 断链原因码（13 个）

| 原因码 | 含义 |
|---|---|
| `NoSourceForExternalSystem` | 外部系统（硬件/微服务）源码不在本项目。 |
| `RuntimeStateDependentDispatch` | 目标由运行时状态决定。 |
| `MultipleDiRegistrations` | DI 存在多个注册实现。 |
| `ReflectionConstruction` | 经反射创建或取得对象。 |
| `DeferredExecution` | 延迟执行。 |
| `DataMediatedByDatabase` | 数据经数据库表中介。 |
| `StringKeyedDispatchUnmatched` | 事件路径在接收端没有任何画面处理。 |
| `NativeInterop` | 原生代码调用。 |
| `UnresolvedSymbol` | 调用目标无法绑定。 |
| `UnresolvedConstantArgument` | 参数无法静态确定。 |
| `ScreenNotRegistered` | 跳转目标画面不在任何注册表中。 |
| `TruncatedByDepthLimit` | 调用链超过深度上限截断。 |
| `TruncatedByCandidateLimit` | 候选数超过上限截断。 |

## 规则

1. **事实只来自工具结果。** 画面、类、方法、文件、行号、按钮文字、事件路径、API、错误码，全部原样取自工具返回；工具没有返回的内容写“索引中无此信息”。
2. **不读源码文件，不推测。** 不得根据命名习惯推断调用关系或画面。
3. **确认度原样转述。** `candidate` 写“候选”，`certain` 写“确定”，`unresolved` 写“未解析”；任何 `candidate` 不得写成确定语气。
4. **截断必须告知。** `meta.truncated` / `truncatedBy` / `edgesTruncated` / `meta.truncatedByMcp` 任一为真 → 在报告“未解析与截断事项”中列出，并不得声称“只有这些路径”。
5. **索引状态必须告知。** `derivation = derived` 或 `stale = true` → 报告开头醒目提示。
6. **断链必须逐条列出**，每条给出：类别、断点（文件:行 + 命名空间.类.方法）、契约、原因（使用工具返回的 `why` 原文）、回流候选。
7. 调用次数上限 25 次；达到上限仍未完成 → 输出已得到的部分并说明。
