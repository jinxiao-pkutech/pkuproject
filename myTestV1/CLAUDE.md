# CLAUDE.md — ScreenFlow 编码规则（Claude Code 必读）

本文件是你（Claude Code）在本目录工作时的**最高规则**。每次会话开始先完整读本文件，再读 `docs/PROGRESS.md`。

## 1. 你在做什么

实现 **ScreenFlow**：一个对 Unity C# POS 项目做**静态解析**的工具。
输入「开始画面 ID A」「结束画面 ID Z」，输出 A→Z 经过的所有画面，以及每一跳的源文件、命名空间、类、方法、API、硬件交互，并**指出所有断链的位置和原因**。
最终由 VS Code GitHub Copilot 的自定义 Agent 通过 MCP 调用。

## 2. 最重要的前提：你看不到被解析的源码

- 被解析的 POS 源码（约 9,400 个 C# 文件）**不在你这台机器上**。
- 关于 POS 源码的**唯一事实来源**是 `docs/03_源码事实手册.md`。手册里没有写的形态，**不要猜测、不要编造**。
- 所有与 POS 项目相关的类型名、方法名、文件路径、常量，一律写进 `config/unitypos.json`，**禁止硬编码在 C# 里**。
- 遇到手册没有覆盖、但实现必须做出假设的点：按“可配置 + 产生诊断”实现，并在 `docs/PROGRESS.md` 的「待本机确认」中登记一行。
- 你的测试只能针对 `tests/fixtures/MiniPos/`（按手册仿造的迷你工程）。真实源码上的验收由用户在另一台机器执行。

## 3. 工作循环（每个任务严格按此执行）

1. 读 `docs/PROGRESS.md`，找到第一个状态为 `TODO` 且所有前置任务均为 `DONE` 的任务。
2. 把该任务状态改为 `DOING`，写入开始时间。
3. 读 `docs/tasks/<任务ID>.md`，以及其中「必读」列出的文档章节。不要读与本任务无关的大段文档。
4. 只修改任务书「产出」中列出的文件/目录。需要改动列表外的文件时，在 PROGRESS 的备注中写明原因。
5. 执行任务书中的「冒烟命令」。
6. 冒烟失败：分析原因并修正，再执行。**同一任务最多 3 轮修正**。3 轮后仍失败：
   - 状态改为 `BLOCKED`，在备注中写明失败命令、错误摘要、已尝试的修正；
   - **停止整个工作循环**，不要开始下一个任务。
7. 冒烟通过：状态改为 `DONE`，写入完成时间与冒烟结果摘要（通过的测试数）。
8. 自动回到第 1 步，开始下一个任务。

全部任务 `DONE`（或遇到 `BLOCKED`）时结束，并在会话最后输出一段总结：完成了哪些任务、PROGRESS 中新增了哪些「待本机确认」。

## 4. 禁止事项

- **禁止 git commit / push / 创建分支**。只改工作区文件。
- 禁止修改 `myTestV1/` 以外的任何文件。
- 禁止修改 `docs/` 下的规格文档（`PROGRESS.md` 除外）。规格有矛盾时，在 PROGRESS 备注中记录并按更严格的一方实现。
- 禁止为了让测试通过而：删除/跳过测试、放宽断言、硬编码期望值、捕获异常后吞掉。
- 禁止在 Core 项目中引用 `Microsoft.CodeAnalysis.*`（有架构测试检查）。
- 禁止用注释内容、被注释掉的代码作为解析事实。
- 禁止按“短类名”匹配符号；一律用符号 ID。
- 禁止把 `candidate`（候选）提升为 `certain`（确定），禁止只保留“最可能”的一个候选。
- 禁止在真实分析路径中注入伪造类型来消除编译错误（fixture 的 `Stubs/` 只用于测试工程本身）。
- 禁止联网下载除任务书中列出的 NuGet / PyPI 包以外的依赖。

## 5. 代码规约

### C#（.NET 8）
- `<Nullable>enable</Nullable>`、`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`、`<LangVersion>latest</LangVersion>`。
- file-scoped namespace；`sealed record` 表示数据；不用可变静态字段。
- 输出必须**确定性**：所有集合在写库/输出前按 ID 排序；禁止依赖 `Dictionary`/`HashSet` 的枚举顺序。
- 只在代码本身表达不出的地方写一行注释；不写复述代码的注释。
- JSON 一律用 `System.Text.Json`，`camelCase`，不输出 null 以外的默认值省略规则需与 `docs/02` 一致。
- 不引入任务书未列出的 NuGet 包。

### Python（3.11+）
- 类型标注；`ruff` 风格；只做调用层，**不得包含解析逻辑**。

## 6. 文档索引

| 文档 | 用途 |
|---|---|
| `docs/01_需求规格书.md` | 做什么、不做什么、功能需求编号 FR-xx |
| `docs/02_架构设计书.md` | 模块、依赖规则、IR、数据库、算法、CLI 契约 |
| `docs/03_源码事实手册.md` | POS 源码真实形态（唯一事实来源） |
| `docs/04_运行时Agent设计书.md` | Python MCP 服务、Copilot Agent、报告格式 |
| `docs/05_测试与验证规范.md` | 测试分层、fixture 规格、黄金数据、本机验收 |
| `docs/06_任务拆分计划.md` | 任务列表与依赖 |
| `docs/tasks/Txx.md` | 单个任务书 |
| `docs/PROGRESS.md` | 进度与状态（你唯一可写的 docs 文件） |

## 7. 命令速查

```
dotnet build ScreenFlow.sln -c Release
dotnet test ScreenFlow.sln -c Release
dotnet test ScreenFlow.sln -c Release --filter "Task=T05"
dotnet run --project src/ScreenFlow.Cli -c Release -- <command> ...
python -m pytest python/tests -q
```
