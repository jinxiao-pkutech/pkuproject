# ScreenFlow 阶段性总结 / 交接说明（更新至全部任务完成）

> 给接手的 LLM：先读 `CLAUDE.md`（最高规则），再读 `docs/PROGRESS.md`，然后读本文件。
> 本文件只是交接便条，**不是规格**；规格永远以 `docs/01`～`docs/06` 与 `docs/tasks/Txx.md` 为准。

## 1. 现状

- **T00 – T23 全部 DONE**。无 BLOCKED。T20–T23 由接手的 LLM 完成，本节数字为**独立复核实测**。
- C# 全量：`dotnet test ScreenFlow.sln -c Release` → **193 passed / 0 failed**
  （CSharp 78、Core 70、UnityPos 30、Cli 12、Architecture 3）。
- Python：`python -m pytest python/tests -q` → **35 passed**。
- MiniPos 验收：`acceptance/run_acceptance.py` → A-01…A-17 全 PASS，A-02/A-18/A-19 按设计跳过（需真实工程），exit 0。
- `index build` 在夹具上跑通 P1–P9，产出 26 张表的完整索引。
- 任务循环已走完。后续工作不再是任务书驱动，而是**在真实 POS 源码上落地**：跑验收、按结果调 `config/unitypos.json` 的锚点、关闭 Q-01…Q-17。

## 2. 环境与命令

- Windows 11 / PowerShell 为主，Bash 可用。.NET SDK 8.0.426 已安装（机器上同时有 .NET 10 SDK，`global.json` 已固定到 8）。Python 3.13。
- 被解析的真实 POS 源码**不在本机**；唯一事实来源是 `docs/03_源码事实手册.md`，测试只能打 `tests/fixtures/MiniPos/`。

```
dotnet build ScreenFlow.sln -c Release
dotnet test  ScreenFlow.sln -c Release
dotnet test  ScreenFlow.sln -c Release --filter "Task=T18"
dotnet run --project src/ScreenFlow.Cli -c Release -- index build --project tests/fixtures/MiniPos --profile fixture --db <out.db>
```

注意 CLI 的选项是 `--project`（不是 `--root`），且目前没有 `--help`（T20 的活）。

## 3. 绝对不能破的规矩（CLAUDE.md §4 原文要点）

- 禁止 git commit / push / 建分支；只改工作区。
- 禁止改 `myTestV1/` 以外的文件；禁止改 `docs/` 下除 `PROGRESS.md` 以外的任何文档。
- 禁止为了过测试而删测试、放宽断言、硬编码期望值、吞异常。
- `ScreenFlow.Core` **禁止**引用 `Microsoft.CodeAnalysis.*`（有架构测试）。依赖方向：`Cli → CSharp → Core`、`Cli → UnityPos → Core`；UnityPos 不得引用 Roslyn 和 CSharp。
- 禁止用注释内容当事实；禁止按短类名匹配符号（真实工程有 125 个重名简单类型名），一律用符号 ID；所有 POS 名字写在 `config/unitypos.json`。
- 禁止把 candidate 提升为 certain，禁止只保留「最可能」的一个候选。
- 规格矛盾时：按更严格的一方实现，并在 PROGRESS 的「待本机确认」登记一行。

## 4. 代码地图

```
src/ScreenFlow.Core/        # 无 Roslyn。IR 模型、SQLite 存储、管线、解析、断链、图
  Ir/                       # 10 种记录 + 17 种 Value kind（02 §5）
  Storage/                  # Schema.sql（嵌入资源，26 表）、IndexDb、IrWriter、IndexReader、Dump
  Pipeline/IndexBuilder.cs  # P1..P9、IDomainStage、DomainStageContext、原子替换、指纹
  Resolution/               # ResolutionIndex(Dispatcher.cs 内)、ValueResolver、FieldPointsTo、IValueProvider
  Breaks/                   # WhyTemplates(9 类别/13 原因/中文 why)、BreakFactory(按 category+reason+callsite+contract 去重)
  Graph/                    # EventPathMatcher、ChainStep、ScreenEdge、TraversalState、ScreenGraphBuilder(P8)
  Pathing/                  # ScreenGraph(折叠图)、Dijkstra、YenKShortest、PartialPathFinder、PathService
src/ScreenFlow.CSharp/      # Roslyn 抽取器
  Compilation/              # rsp 发现解析、预检、编码、CompilationBuilder
  Walkers/                  # SymbolKeys、SymbolCollector、BodyWalker、ConditionCollector、ReachabilityAnalyzer、MarkerScanner、ImplCollector
  Values/ValueBuilder.cs    # IOperation → Value 折叠
src/ScreenFlow.UnityPos/    # 领域适配器（P6 资源 + P7 适配器）
  Anchors/、Resources/、Screens/、Ui/、Tcp/、Di/、KeySeq/、Boundaries/、Entries/
src/ScreenFlow.Cli/Program.cs
tests/…                     # 每个测试带 [Trait("Task","Txx")]
config/profiles.json, config/unitypos.json
```

CLI 当前接线：`new IndexBuilder(new IDomainStage[] { new UnityPosDomain(), new ScreenGraphBuilder() })`。
适配器顺序（`AdapterOrder`）：resources(P6) → screens → enumerations → ui-bindings → di → keyseq → navigation → tcp(+`tcp:receivers`) → device → api → native → sql → queue → reflection → entries。

## 5. 踩过的坑（改动前务必知道）

1. **每个适配器跑完就要 flush `DomainWriter`**。适配器之间靠领域表通信，整段 stage 结束才 flush 会让后面的适配器读到空表（曾导致 controls=0）。
2. **partial 方法在 Roslyn 里是两个符号部件**，文档注释 ID 相同。`SymbolCollector.Canonical()` 统一归到定义部件、合并两部件的声明位置；否则并行遍历时谁后写谁决定 `file`，输出不确定。
3. **调用点 ID 用被调成员名 token 的偏移**（`ValueBuilder.CallStart`），否则 `a.B().C()` 的内外两个调用点 ID 相同（Q-13）。
4. **断链向后传播、条件不传播**（`ScreenGraphBuilder.GraphRun.Walk`）：每个调用点用 `List<string> found` 收集本次新增断链，处理完合并进方法内的游走状态——后一条语句跑在一个无法确定的结果之上；而 `call.Conditions` 是守卫，只约束它所守的那一次调用，逐调用点附加、不累积。
5. **`ScreenGraphBuilder.Build()` 的顺序**：`CheckInvariants()` → 写 edges → 写 breaks → 最后统一 flush。写在 `Writer.Flush()` 之后等于没写。
6. `CompilationBuilder` 里判断「有没有 BCL」只看 BCL 标记，`CompilationReference` 不算 BCL（否则会压掉宿主框架引用兜底，连 `System.Object` 都绑不上）。
7. 用 Bash heredoc 往 C# 里写含 `\n` 的字符串会被转义处理搞坏；改用 Write/Edit 工具或先写 python 脚本再执行。
8. xUnit 分析器是 error 级：`Assert.Single` 里不能直接写 `Where`（xUnit2031），断言「不存在」用 `Assert.DoesNotContain` 而不是 `Assert.Empty`（xUnit2029）。

## 6. 夹具基线（回归用，`index build --profile fixture`）

```
files 66, symbols 803, impls 44, callsites 263, assigns 138, creations 44, cfgs 173,
markers 132, evidence 1068, diagnostics 20,
screens 8, enum_members 18, controls 22, control_handlers 11, texts 14, messages 28,
input_constraints 7, keyseq_transitions 3, di_registrations 8, message_handlers 4,
entries 14, boundaries 7, domain_edges 45, screen_edges 17, breaks 17
breaks 分类：TerminalTcpCurrentScreen 7、DeferredExecution 2、ExternalHardware 2、
            KeySeqStageDependent 2、Unknown 2、DiAmbiguous 1、NativeInterop 1
```

E-01 … E-26 全部已验证（E-26：0903→0907 恰好 2 条链）。E-27 之后的期望事实留给 T19+。

## 7. 接下来做什么

- **T20 CLI 全命令 + 查询 + 确定性**（全量检查点）：`Core/Query/*`、手写参数解析、信封/错误码/退出码、stale 检测、`evidence get` 读源码片段并比对 `snippet_hash`、输出里不得出现绝对路径、`index dump`、`debug stats/callsites/sends`。
- **T21 Python MCP 服务**（02 §12、04 §2/§7）：只做调用层，不得含解析逻辑；测试用 `fake_cli.py`。
- **T22 Copilot 部署文件**（04 §3–§6）。
- **T23 本机验收脚本**（05 §6，全量检查点）。

## 8. 未决事项

- `docs/PROGRESS.md` 的「待本机确认」表有 Q-01 … Q-16（Q-08、Q-10 已关闭）。新发现的假设继续往表里追加一行，不要删既有行。
- **已知风险（未根除）**：`Symbols_Ids_StableAcrossRuns` 曾偶发失败。真因之一（partial 方法）已修；修复后 13 次全量里仍出现过 1 次无详情的失败，不能断言已根除。该断言已改成会打印「哪个符号、两次分别是什么」，下次复现可直接定位。T20 的 `Determinism_TwoBuilds_ProduceIdenticalDump` 会从导出侧再卡一道。

## 9. 阶段里程碑日志

### T18 画面操作图与断链（2026-10-23 13:10，9 passed / 全量 175）
- 新增 `Core/Graph/{ChainStep,ScreenEdge,TraversalState,ScreenGraphBuilder}.cs`，CLI 把 `ScreenGraphBuilder` 接在 `UnityPosDomain` 之后。
- 夹具：screen_edges=17、breaks=17。E-26…E-30、E-36、E-37 成立。
- 关键修复：断链必须向后传播（同一方法里后续调用点要看得见前面的断链），条件则不传播。

### T19 路径搜索（2026-10-23 14:05，12 passed / 全量 187）
- 新增 `Core/Pathing/{ScreenGraph,Dijkstra,YenKShortest,PartialPathFinder,PathService}.cs`。
- 折叠图：同 (from,to) 的平行边合成一条，cost 取最小；悬空边不参与搜索，只用于 partialPaths。
- 确定性：Dijkstra 代价相等时比最小 edgeId；Yen 候选按 (cost, 边数, edgeId 序列)。有正序/逆序建图的对拍测试。
- E-31…E-34 全部成立（含 `triggerText.plain` = `その他の金額を入力
(1,000円単位)`、跨终端路径 0001→0089→0903→0907）。
- `overallConfidence` 的判定在规格里没写死，采用：一跳有任一条 certain 且无断链的平行边 ⇒ 该跳 certain；全跳 certain ⇒ 路径 certain。
- 新增 Q-17（§11.4 的 from/to 是单对象 vs §11.5-1 要合并多终端；实现取列表，CLI 层再决定输出形态）。

## 10. T20 执行计划（下一个接手者直接照做）

T20 **尚未开始**（PROGRESS 里仍是 TODO，工作区没有任何 T20 的改动）。它是「全量检查点」任务，量最大。

### 现有基础
`src/ScreenFlow.Cli/Program.cs`（479 行）已经有：`Main` 的异常→错误码映射、`Run` 的命令分发（目前只有 `index build` / `index status` / `index dump`）、`Succeed`/`Fail`/`Meta`/`Write` 信封输出、`ErrorCodes`、`ExitCodes.For`、`CliException`、`CliOptions.Parse`（手写解析，已支持 `--project --profile --db --config-dir --locale --out --parallel --pretty`）、`Staleness()`（02 §7.6 指纹重算）。
**要做的是往这套骨架上加命令，不是重写它。**

### 要加的东西
1. `CliOptions` 增加：`--id --terminal --direction --limit --from --to --terminal-from --terminal-to --k --max-hops --include-prefix --detail --category --screen --edge --name --kind --code --key --collection --context --file --from-line --to-line --key-contains --target-screen`。
2. `src/ScreenFlow.Core/Query/` 七个查询类（`ScreenQuery`、`BreakQuery`、`SymbolQuery`、`EvidenceQuery`、`TextQuery`、`MessageQuery`、`DebugQuery`），全部只读 `IndexReader`，返回可直接序列化的 record。
3. `Run` 里接上 02 §11.2 表格的全部命令：`screen get/controls/constraints/transitions`、`path find/prefix`（直接用 T19 的 `PathService`）、`break list/get`、`symbol find/get/callers/callees`、`evidence get`、`text get`、`message get`、`debug stats/callsites/sends`。
4. `evidence get`：按记录的编码解码源文件、算片段哈希与 `evidence.snippet_hash` 比较，不一致 → `meta.stale=true`。注意 `snippet_hash` 是 T03 留的钩子，确认 T11 是否真的注入了 hasher，没有就要在这里补。
5. 输出里**所有路径必须是工程相对路径**（`Ids.Normalize`，`/` 分隔）；有一个测试专门扫 data 里不许出现盘符或以 `/` 开头的路径。
6. 同一画面 ID 存在于两个终端且未指定 `--terminal` → **返回数组**，不报 `E_AMBIGUOUS`（任务书第 2 条）。Q-17 的 from/to 形态在这里定。

### 测试（`tests/ScreenFlow.Cli.Tests/`）
`Cli_AllCommands_ReturnValidEnvelope`、`Cli_ErrorCodesAndExitCodes`（无效参数=2、画面不存在=3、无数据库=E_INDEX_NOT_FOUND）、`Cli_NoAbsolutePathsInOutput`、`Cli_Stale_WhenFixtureFileTouched`（把夹具复制到临时目录构建后改一个文件）、E-35 `Determinism_TwoBuilds_ProduceIdenticalDump`、`Cli_ScreenControls_0903_TextJa`。

### 冒烟（全量检查点，任务书原文）
```
dotnet build ScreenFlow.sln -c Release
dotnet test  ScreenFlow.sln -c Release
dotnet run --project src/ScreenFlow.Cli -c Release -- index build --project tests/fixtures/MiniPos --profile fixture --db .screenflow/fixture.db
dotnet run --project src/ScreenFlow.Cli -c Release -- path find --from SSAW0903 --to SSAW0907 --profile fixture --db .screenflow/fixture.db --detail full --pretty
```
完成定义：全部测试通过，且最后一条命令 `ok: true` 且第 1 跳有 2 条 edges。

### 注意
- E-35 的确定性导出会第二次卡住那个**未根除的 `Symbols_Ids_StableAcrossRuns` 偶发问题**（见第 8 节）。如果 E-35 失败，先看两次 dump 的 diff 落在哪张表，那就是真因的直接证据。
- `path find` 的 data 直接由 `PathService.Find` 返回的 `PathFindResult` 序列化即可（字段上已按 02 §11.4 标了 `JsonPropertyName`），CLI 只需决定 from/to 单对象还是数组（Q-17）。

### T20 CLI 全命令与查询（6 passed / C# 全量 193）
- 新增 `Core/Query/` 七个类 + `Core/Storage/SnippetHasher`；CLI 接上 02 §11.2 的全部命令与选项。
- `IrWriter` 的 snippet 哈希推迟到 flush（files 落库之后）计算 —— 夹具上 1068 条 evidence 全部有 `snippet_hash`。
- Q-17 定案：单终端 → `from`/`to` 输出单个对象；多终端 → 数组。
- E-35（两次构建导出 SHA-256 相同）通过。

### T21 Python MCP 服务（28 passed）
- `python/screenflow_mcp/{__init__,__main__,server,cli_runner,schemas,truncate}.py`，mcp 1.30.0。
- 测试全部打 `fake_cli.py`，不依赖真实 CLI。
- **实测补充**：拿真实 CLI + 夹具索引跑 `--selftest`，4 步全 ok。注意 `SCREENFLOW_CLI` 指 dll 时文件名是 **`screenflow.dll`**（程序集名按 02 §11 固定为 `screenflow`），写成 `ScreenFlow.Cli.dll` 会得到 `E_CLI_FAILURE: 找不到指定的命令或文件`。

### T22 Copilot 部署文件（4 passed）
- `deploy/.github/agents/screen-flow.agent.md`、`deploy/.github/skills/screen-flow/SKILL.md`、`deploy/.vscode/mcp.json`、`deploy/README.md`。
- frontmatter 与 mcp.json 逐字采用 04 §3.1 / §5 / §6；Skill 术语表覆盖 9 个断链类别 + 13 个原因码。

### T23 本机验收脚本（3 passed）
- `acceptance/{run_acceptance,check_report,strip_comments}.py` + `expectations.{pos,minipos}.json` + README。
- 断言 DSL：`exists`/`equals`/`contains`/`count>=`/`allNot`，JSON 路径支持点号与 `[*]`。
- A-11…A-16 的交叉核对（去注释后正则计数、`#if TeamUI` 区间）在脚本里实现并在夹具上通过。

### 复核结论（2026-10-23，独立执行）
- `docs/` 下除 `PROGRESS.md` 外全部未改动（mtime 仍是 10-22）；分支上**没有任何 commit**（规则要求）。
- 三类冒烟全部重跑通过：`index build`（66 文件 / 803 符号 / 17 边 / 17 断链 / 1068 证据）、`path find 0903→0907 --detail full`（ok=true、第 1 跳 2 条 edges、`triggerText` = `その他の金額を入力
(1,000円単位)`、路径断链 5 条、链 8 步）、MCP `--selftest` 4/4。
- **唯一剩余事项**：在真实 9,400 文件工程上执行验收并关闭 Q-01…Q-17。使用说明见 `MANUAL.md`。
