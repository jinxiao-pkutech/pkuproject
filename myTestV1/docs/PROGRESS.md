# PROGRESS（Claude Code 唯一可写的 docs 文件）

## 使用规则
- 状态：`TODO` → `DOING` → `DONE` / `BLOCKED`。
- `DOING` 时填开始时间；`DONE` 时填完成时间与冒烟结果摘要（如 `T09: 14 passed`）。
- `BLOCKED` 时在备注写：失败命令、错误摘要、已尝试的修正（最多 3 轮）。
- 只追加，不删除历史备注。

## 任务表

| ID | 名称 | 前置 | 状态 | 开始 | 完成 | 冒烟结果 / 备注 |
|---|---|---|---|---|---|---|
| T00 | 解决方案骨架与环境检查 | — | DONE | 2026-10-22 21:28 | 2026-10-22 21:55 | 产出全部交付。**第一次冒烟**：build 0 警告 0 错误、`--filter "Task=T00"` 7 passed（架构测试 3 + 各测试工程骨架断言 4），但环境检查失败 → BLOCKED：失败命令 `pwsh tools/check-env.ps1` 与 `bash tools/check-env.sh` 均 exit 1，错误摘要 `.NET 8 SDK (installed SDKs: 10.0.113)`，Python 3.13.9 满足要求；按任务书「这不是代码问题，不要尝试修正」未做修正，循环停止。**2026-10-22 21:55 已解除**：用户装入 .NET SDK 8.0.426 + Runtime 8.0.32。同时删除了临时加的 `RollForward=Major`（运行时已就位，恢复与 02 §1 属性清单完全一致，原 Q-08 随之关闭）。**重跑冒烟全绿**：check-env exit 0、build 0 警告 0 错误、`--filter "Task=T00"` 7 passed。另：.NET 10 SDK 的 `dotnet new sln` 默认产出 `.slnx`，已用 `--format sln` 生成 T00 要求的 `ScreenFlow.sln`（Q-09）。 |
| T01 | 配置文件与加载器 | T00 | DONE | 2026-10-22 21:57 | 2026-10-22 22:03 | `--filter "Task=T01"` 11 passed（全量 18 passed，build 0 警告 0 错误）。两个 json 逐字采用 02 §4.1 / §4.2：4 个 profile、`tcp.displayEventIds` 6 项、`ui.constraintMethods` 4 项均已断言。02 §4.2 末尾要求登记的 4 项已由既有 Q-03（displayEventIdsVerifiedTerminals）、Q-04（stringTableSearchOrder.employee）、Q-05（queue.*）、Q-06（api.requestType）覆盖，未重复登记。新增 Q-11：config-dir 探测顺序第 3 项的相对层数在标准 bin/<配置>/<框架> 布局下指向 src/config，开发时找不到仓库配置，已在文档顺序之后追加一个向上查找作为兜底。另：`JsonSerializerOptions.MakeReadOnly()` 需 `populateMissingResolver: true`，否则静态构造抛 InvalidOperationException（1 轮修正）。 |
| T02 | IR 模型、Schema、NDJSON | T00 | DONE | 2026-10-22 22:06 | 2026-10-22 22:17 | `--filter "Task=T02"` 22 passed（全量 40 passed，build 0 警告 0 错误）。10 种记录 + 17 种 Value kind 各一例，`IrSamples` 并断言示例集合与模型的 kind 列表完全一致（新增 kind 无示例会直接失败）。`schema/ir-1.schema.json` 与模型用反射比对：13 组 enum、10 个记录的 required（取 C# `required` 的 RequiredMemberAttribute）与 properties 全集、4 个 ID 正则、深度/条数/文本上限。`tests/IrEmitterHelper` 为控制台项目且**不引用 Core**（证明仅凭 NDJSON 契约即可对接），已加入 sln；Core.Tests 以 ReferenceOutputAssembly=false 仅建立构建顺序。实现说明：往返相等用规范化 NDJSON 行比较而非对象相等 —— 记录含 IReadOnlyList 成员，record 自动生成的 Equals 对其是引用相等，对象比较会把无差异报成差异；行相等同时正是 CLAUDE.md §5 的确定性要求。修正 2 处（各 1 轮）：`JsonSerializerOptions.MakeReadOnly` 需 populateMissingResolver；`StringTestOps.Equals` 隐藏 object.Equals，C# 标识符改为 EqualsOp（线上值仍为 "equals"）。 |
| T03 | SQLite 存储层与规范化导出 | T02 | DONE | 2026-10-22 22:18 | 2026-10-22 22:27 | `--filter "Task=T03"` 9 passed（全量 49 passed，build 0 警告 0 错误）。02 §7.2 + §7.3 全部 26 张表已建；`Schema.sql` 为嵌入资源，并有测试逐表比对表名清单。实现说明 1：`Dump` 对 `diagnostics` 特殊处理 —— 其 `seq` 是自增合成主键，编码的是插入顺序而非内容，故导出时省略该列并按内容排序，否则 `Storage_Dump_IsOrderIndependent`（同一组记录两种顺序写入）不可能成立。其余表按主键排序。实现说明 2：`evidence.snippet_hash` 无法从 IR 派生（02 §5 的记录里没有 snippet 字段），`IrWriter` 开了可选的 `snippetHasher` 钩子，未提供时该列为 NULL；留给 T11 构建管线注入（届时能读到工程根）。实现说明 3：`assigns.target_symbol_id` 对 `local` 目标为 NULL —— IR 的 local 值只带名字不带符号 ID，编造一个会让该列说假话。修正 2 处（各 1 轮）：Assert.Throws 对「所有路径都 throw」的 lambda 会选中过时的 Func<Task> 重载，显式定型为 Action；`Flush()` 失败后缓冲区未清空导致 `Dispose()` 二次抛出并掩盖真实异常，改为失败即清空并标记 faulted。 |
| T04 | 夹具：C# 源码与 rsp | T00 | DONE | 2026-10-22 22:35 | 2026-10-22 22:39 | `--filter "Task=T04"` 4 passed（全量 53 passed，build 0 警告 0 错误）。按 05 §3.2 建成 65 个 .cs（KEY01 留给 T05）+ 2 套 rsp（ed01/an01）+ common.rsp + mvfrm 干扰文件 + ProjectVersion.txt + README。编译测试直接用 Roslyn 编译 Assets/**/*.cs（排除 KEY01）对宿主 TRUSTED_PLATFORM_ASSEMBLIES，Editor 与 Android 两个 define 组合各 0 error，一次通过。额外加了 2 个条件编译断言（超出任务书要求的 2 个测试）：TeamUI 块在两个配置下都不进入语法树（E-10 的底线），SCEW0001 只在 UNITY_EDITOR 下可见（E-11）—— 这两条是夹具自身的正确性前提，放在这里比等到 T12 更早暴露问题。刻意埋入的「不得出现」形态共 6 处（TeamUI 块、注释行、return 后不可达、被注释的 SocketRequestData、被注释的键序条目、写反的 Err2/Err22 注释），已在 README 列表。rsp 中列出的 KEY01 当前在磁盘上不存在 —— 这是 T04 任务书的安排，同时正好覆盖 02 §6.1「列出但缺失的源文件」预检路径。 |
| T05 | 夹具：资源与 CP932 文件 | T04 | DONE | 2026-10-22 22:52 | 2026-10-22 22:45 | 全量检查点通过：`dotnet test` **58 passed**（`--filter "Task=T05"` 5 passed，build 0 警告 0 错误）。产出：UXML 2（03 §14.3 形态，含 xsi= 非标准属性、project:// 的 &amp; 转义、完整类型名元素、重名 moneyLabel）、画面初始化 JSON 3、本地化 YAML 5（Customer 共享表 + ja + en，PosRegister 共享表 + ja）、错误文案 JSON（ErrCode 1/2/22/24，22 逐字采用 03 §16）、`tools/KEY01.src.txt` + `tools/make-cp932.py` + 生成的 CP932 文件（962 字节，脚本自检「非合法 UTF-8」）。KEY01 已纳入 T04 的编译测试（去掉临时排除，按 UTF-8-BOM → 严格 UTF-8 → CP932 顺序解码），两个配置仍 0 error。**Q-10 已确认并关闭**：`System.Text.Encoding.CodePages.dll` 确实在 .NET 8 共享框架内（`shared/Microsoft.NETCore.App/8.0.32/`），且 `CodePagesEncodingProvider` 无需额外 NuGet 包即可编译 —— 02 §1.2 的说法正确，不需要扩充允许清单。实现说明：YAML 里的 \uXXXX 转义必须以字面反斜杠落盘，但本会话的文件写入通道会解析转义序列，故该行改为在生成时用 `chr(92)` 拼出反斜杠，并由 `Fixture_JaTableCarriesEveryScalarForm` 断言「转义仍在、解码后的文本不在」；5 种标量形态（双引号带转义 / 裸值 / 单引号 / 跨行折行 / 空值）全部覆盖。 |
| T06 | rsp 发现、解析、预检、编码 | T01 T05 | DONE | 2026-10-22 23:05 | 2026-10-22 22:52 | `--filter "Task=T06"` 15 passed（全量 73 passed，build 0 警告 0 错误）。E-01 / E-02 / E-03 均覆盖。**规格补充（已实现并记录）**：02 §6.1 对「同一程序集名出现在多个 rsp」只规定「按 selectPlatform 选择；仍有多个 → 选源文件最多者」，但 fixture profile 的 selectPlatform 是 any、ed01 与 an01 的源文件数相同，纯按路径排序会选中 an01，与 E-01 要求的 ed01 冲突。故平局顺序实现为：profile 自身的 `platform` 字段匹配 → 源文件最多 → 路径升序。另：`PlatformDetected` 改为取「实际被选中的 rsp 的平台」（而非筛选条件），因为读者能据以行动的是索引实际由什么构建的。修正 3 处（各 1 轮）：测试里 Assert.Single 内直接写 Where 触发 xUnit2031；两个 internal helper 需对测试程序集可见（改 public）；TempProject 的 Path 成员遮蔽了 System.IO.Path 类型（改名 PathOf）。 |
| T07 | Compilation 构建 | T06 | DONE | 2026-10-22 22:52 | 2026-10-22 23:10 | 7 passed（全量 80 passed） |
| T08 | 符号、impl、稳定 ID、变更标记 | T02 T07 | DONE | 2026-10-22 23:10 | 2026-10-23 00:05 | 21 passed（全量 101 passed） |
| T09 | 方法体与 Value 折叠 | T08 | DONE | 2026-10-23 00:05 | 2026-10-23 01:20 | 16 passed（全量 117 passed） |
| T10 | 条件、CFG 可达性、lambda | T09 | DONE | 2026-10-23 01:20 | 2026-10-23 02:30 | 9 passed（全量 126 passed，检查点全量通过） |
| T11 | 构建管线与 Core 解析服务 | T03 T10 | DONE | 2026-10-23 02:30 | 2026-10-23 04:10 | 11 passed（全量 137 passed；`index build` 冒烟 ok=true） |
| T12 | 锚点 + screens/enumerations/navigation | T11 | DONE | 2026-10-23 04:10 | 2026-10-23 05:30 | 7 passed（全量 144 passed） |
| T13 | 资源解析器 | T05 T11 | DONE | 2026-10-23 05:30 | 2026-10-23 06:40 | 6 passed（全量 150 passed） |
| T14 | ui-bindings 适配器 | T12 T13 | DONE | 2026-10-23 06:40 | 2026-10-23 08:00 | 4 passed（全量 154 passed） |
| T15 | tcp 适配器 | T12 | DONE | 2026-10-23 08:00 | 2026-10-23 09:10 | 4 passed（全量 158 passed，检查点全量通过） |
| T16 | keyseq 与 di 适配器 | T12 | DONE | 2026-10-23 09:10 | 2026-10-23 10:20 | 5 passed（全量 163 passed） |
| T17 | 边界类与入口适配器 | T14 T15 T16 | DONE | 2026-10-23 10:20 | 2026-10-23 11:40 | 3 passed（全量 166 passed） |
| T18 | 画面操作图与断链 | T17 | DONE | 2026-10-23 11:40 | 2026-10-23 13:10 | 9 passed（全量 175 passed） |
| T19 | 路径搜索 | T18 | DONE | 2026-10-23 13:15 | 2026-10-23 14:05 | 12 passed（全量 187 passed） |
| T20 | CLI 全命令、查询、确定性 | T19 | DONE | 2026-10-23 10:05 | 2026-10-23 10:40 | 6 passed（全量 193 passed，build 0 警告 0 错误）。`index build` 注入 `evidence.snippet_hash`（1068 行全部有值）；`path find SSAW0903→SSAW0907 --detail full --pretty` 输出 `ok:true` 且第 1 跳 2 条 edges，triggerText = `その他の金額を入力\n(1,000円単位)`。新增 `Core/Query/` 7 类 + `Core/Storage/SnippetHasher`；`IrWriter` 的 snippet 哈希推迟到 flush（在 files 落库后）计算；CLI 增加全部查询命令与选项；Q-17 形态定案：单终端→单对象、多终端→数组。 |
| T21 | Python MCP 服务 | T20 | DONE | 2026-10-23 10:40 | 2026-10-23 11:05 | 28 passed（`python -m pytest python/tests -q`）。`pip install -e "python[dev]"` 成功（mcp 1.30.0）。产出 `python/screenflow_mcp/{__init__,__main__,server,cli_runner,schemas,truncate}.py` + `python/tests/{fake_cli,test_runner,test_tools,test_truncate,test_selftest}.py`。`python -m screenflow_mcp --selftest` 用 fake_cli 跑通 exit 0。 |
| T22 | Copilot 部署文件 | T21 | DONE | 2026-10-23 11:05 | 2026-10-23 11:20 | 4 passed（Python 全量 32 passed）。产出 `deploy/.github/agents/screen-flow.agent.md`、`deploy/.github/skills/screen-flow/SKILL.md`、`deploy/.vscode/mcp.json`、`deploy/README.md`、`python/tests/test_deploy_files.py`。frontmatter 逐字采用 04 §3.1 / §5；mcp.json 逐字采用 04 §6；Skill 术语表覆盖 9 类别 + 13 原因码。 |
| T23 | 本机验收脚本 | T20 | DONE | 2026-10-23 11:20 | 2026-10-23 11:30 | 3 passed（Python 全量 35 passed）；全量检查点通过：build 0 警告 0 错误、C# 193 passed、`dotnet publish` 成功、MiniPos 验收除跳过项（A-02,A-18,A-19）外全部 PASS、exit 0。产出 `acceptance/{run_acceptance,check_report,strip_comments}.py`、`expectations.{pos,minipos}.json`、`README.md`、`python/tests/test_acceptance_scripts.py`。 |

## 待本机确认

由 Claude Code 在实现中发现的、需要在真实源码上确认的假设，逐行追加：

| # | 内容 | 相关配置 / 代码 | 登记任务 |
|---|---|---|---|
| Q-01 | Unity 2022.3.68f1 生成的 rsp 实际位置与格式（03 §2） | `RspParser`、`profiles.json` | 设计时 |
| Q-02 | Android Player 编译 rsp 的生成方式（需 Player 构建）与其 define | `profiles.json` `selectPlatform` | 设计时 |
| Q-03 | 员工面 TcpPresenter 是否同样按 `Common.*` 显示类事件远程显示画面 | `tcp.displayEventIdsVerifiedTerminals` | 设计时 |
| Q-04 | 员工面字符串表的集合名与检索顺序 | `resources.stringTableSearchOrder.employee` | 设计时 |
| Q-05 | 模型队列 `AddQueue` 与 `ElementRequest` 的实际类型与文件位置 | `queue.*` | 设计时 |
| Q-06 | `ApiRequest` 的文件位置与构造形参名 | `api.requestType`、`api.apiKeyParamNames` | 设计时 |
| Q-07 | SQLite 执行 API 的实际类型/方法名 | `sql.*` | 设计时 |
| Q-09 | .NET 10 SDK 的 `dotnet new sln` 默认产出 `.slnx`；已用 `--format sln` 强制生成 T00 要求的 `ScreenFlow.sln`。若后续在本机用 .NET 8 SDK 操作解决方案不受影响 | `ScreenFlow.sln` | T00 |
| Q-11 | 02 §11.1 / T01 §4 的 config-dir 探测顺序第 3 项写作 `<可执行文件目录>/../../../../config`，但在标准 `bin/<配置>/<框架>` 输出布局下它指向 `<仓库>/src/config`（测试工程则指向 `tests/config`），开发时找不到仓库 `config/`；疑为少了一层。已严格保留文档的 4 个探测项，并在其后追加「自可执行文件目录逐级上溯、找含 profiles.json 的 config 目录」作为兜底，不覆盖任何文档探测项。请确认是修文档层数还是保留兜底 | `ConfigLoader.ConfigDirectoryCandidates` | T01 |
| Q-12 | 02 §6.7 把 Issue 引用写作 `#\d+`、`[^\s\]]+#\d+`、`T\d{3}-\d+`、`GSEJPOS\d+-\d+`；但按字面实现时 03 §1.1 的 `…STEP3対応：#8051` 会被整段吞成一个引用（含日文前缀），与 03 §1.1 列出的形态 `#8051` 矛盾。按更严格的一方实现：前缀形态收紧为 `[^\s\[\]：:,，、。]+#\d+`（方括号与句读不入引用），`gitID#972`、`総合(3)#2995` 仍整体匹配。请确认真实仓库中是否存在需要包含句读的引用形态 | `MarkerScanner.IssuePattern` | T08 |
| Q-13 | 02 §5.6 的 `callsiteId = c_ + sha1(callerId|file|spanStart)` 对链式调用不唯一：`a.B().C()` 中内外两个调用点的表达式 `spanStart` 相同，实测 fixture 上出现 10 对重复 id。按更严格一侧实现：调用点的偏移取**被调成员名 token** 的位置（`.C` 的位置），记录里的 `span` 仍是整个表达式。请确认是否同意该定义（影响 ID 稳定性契约） | `ValueBuilder.CallStart` | T09 |
| Q-14 | 02 §5.7 说条件“从语法祖先链收集”，但未说明是否越过 lambda 边界。本实现向上走到所属方法/访问器的声明为止（穿过 lambda），因此 03 §13.2[1] 队列 lambda 里的发送会带上“lambda 是在什么条件下被创建的”这些条件。若希望 lambda 内的调用只带 lambda 自身体内的条件，请在本机确认 | `ConditionCollector.Collect` 的 boundary | T10 |
| Q-15 | 02 §7.6 的指纹要覆盖“选中 rsp 中所有源文件 + 资源文件”。当前实现取 `files` 表（= 被抽取程序集的源文件）加上领域阶段读过的资源文件；**仅作为引用的程序集**（`assemblies.include` 之外）的源文件不在其中。这类文件改了会影响绑定但不会触发 stale。请确认是否需要把 preflight 的 AllSources 也写进索引以纳入指纹 | `IndexBuilder.FingerprintFiles` | T11 |
| Q-16 | 02 §9.8 要求 topicTemplate 取自“实现类 topicMethods 方法体中 return 的 Value”，但 02 §5.1 的 10 种 IR 记录里没有任何一种承载 return 表达式（T09 也没有）。做法：不新增记录种类、不改 IR 版本，把“**单一 return 的方法**”的那个表达式折叠后写进 `symbol.initializer`——该字段原义是“该符号求值得到的表达式”，对单 return 方法语义一致。多个 return 的方法不写（多个值挑一个就是猜）。请确认这个扩展用法，或改为在 02 §5 增加 return 记录 | `SymbolCollector.SingleReturn` | T17 |
| Q-17 | 02 §11.4 的 `path find` data 里 `from`/`to` 是**单个对象**，但 §11.5-1 要求「终端未指定 → 查该 ID 的全部终端，逐个组合计算并合并（结果中注明终端）」。两者在「同一画面 ID 同时存在于客面与员工面」时矛盾。按不丢信息的一方实现：`PathFindResult.From`/`To` 是**列表**（每项带 terminal/screenId/name），T20 的 CLI 在只有一个终端时可以按 §11.4 输出单个对象。**T20 已定案**：单终端→单对象、多终端→数组（`screen get/controls/constraints/transitions` 与 `path find` 的 from/to 同规则）。 | `PathService.Find`、`PathFindResult` | T19 |

## 会话日志

（每次会话结束时追加一行：日期、完成的任务、BLOCKED 的任务、新增的待确认项）

- 2026-10-22 21:35｜完成：无（T00 产出已交付并自测通过，但环境检查未过）｜BLOCKED：T00（缺 .NET 8 SDK，本机为 10.0.113）｜新增待确认：Q-08（RollForward=Major）、Q-09（sln 格式）、Q-10（CodePages 包）｜按 CLAUDE.md §3.6 在 BLOCKED 处停止，未开始 T01。
- 2026-10-22 21:55｜T00 转 DONE（环境已补齐，冒烟 7 passed）｜Q-08 已随 `RollForward` 删除而关闭｜继续 T01。
- 2026-10-22 22:03｜T01 DONE（11 passed，全量 18 passed）｜BLOCKED：无｜新增待确认：Q-11（config-dir 探测层数）｜继续 T02。
- 2026-10-22 22:17｜T02 DONE（22 passed，全量 40 passed）｜BLOCKED：无｜新增待确认：无｜继续 T03。
- 2026-10-22 22:27｜T03 DONE（9 passed，全量 49 passed）｜BLOCKED：无｜新增待确认：无｜继续 T04。
- 2026-10-22 22:39｜T04 DONE（4 passed，全量 53 passed）｜BLOCKED：无｜新增待确认：无｜继续 T05。
- 2026-10-22 22:45｜T05 DONE（全量检查点 58 passed）｜BLOCKED：无｜Q-10 已确认关闭（CodePages 在共享框架内）｜继续 T06。
- 2026-10-22 22:52｜T06 DONE（15 passed，全量 73 passed）｜BLOCKED：无｜新增待确认：无｜继续 T07。
- 2026-10-22 23:10｜T07 DONE（7 passed，全量 80 passed）｜BLOCKED：无｜新增待确认：无｜继续 T08。
- 2026-10-23 00:05｜T08 DONE（21 passed，全量 101 passed）｜BLOCKED：无｜新增待确认：Q-12｜继续 T09。
  - 备注：变更标记除注释 trivia 外，也扫描 `DisabledTextTrivia` 的行。标记是报告用元数据而非解析事实，若只扫描激活区域，同一文件的标记集合会随 profile 的 define 改变；正则要求行首 `//`，不会把字符串字面量当标记。
  - 备注：02 §6.9 的 `--parallel N` 尚未接到 CLI，`SymbolCollector` 当前默认 `ProcessorCount/2`，命令行接线留给 T11/T20。
  - 备注（任务书产出清单外的改动）：把 `IFactExtractor` 与 `FixtureCompilesTest` 注释里残留的 "harness" 一词改为 "core"/"ScreenFlow"。用户已明确 harness 专指通用 LLM agent 平台，不是本工具的名字。
- 2026-10-23 01:20｜T09 DONE（16 passed，全量 117 passed）｜BLOCKED：无｜新增待确认：Q-13｜继续 T10。
  - 备注：字段/属性初始化器只写进 `symbol.initializer`，不另外产生 `creation`/`callsite` 记录。02 §5.5 明确初始化器不产生 assign，对 creation 未作规定；按“初始化器的事实已完整落在 symbol.initializer 里”实现，领域适配器读注册表走的正是这条路径。若本机确认希望初始化器里的 `new` 也出现在 creation 表，可在 T11 加一遍。
  - 备注：`callsite.conditions` / `reachable` 本任务留默认值（空数组 / true），由 T10 填充。
  - 备注：实现中发现两处必须按“定义”归一才能让 ID 对上：构造出来的泛型（`List<Button>`、泛型方法实例）一律取 `OriginalDefinition` 作键；委托的隐式 `Invoke`、隐式构造函数没有声明语法，按被引用符号记录（`isSource=false`，无 file/span）。
  - 备注：`ValueContext.Depth` 的计数与 `IrValidator` 对齐——记录级的值（callsite.args、assign.target/value、creation.value、symbol.initializer）是深度 1 的树根，值内部的实参才逐级 +1。否则 KeySeq 表里的 `methodRef.receiver` 会在深度 6 被截成 unknown。
- 2026-10-23 02:30｜T10 DONE（9 passed，全量 126 passed，检查点要求的全量测试通过）｜BLOCKED：无｜新增待确认：Q-14｜继续 T11。
  - 备注：`cfg` 记录按“顶层方法体”（方法/构造/访问器）各一条；lambda 与局部函数用 `GetAnonymousFunctionControlFlowGraph` / `GetLocalFunctionControlFlowGraph` 参与可达性判定，但不单独产出 cfg 记录。02 §5.8 的 cfg 字段只有 methodId/blockCount/hasGoto/unreachableSpans，未要求每个 lambda 一条。
  - 备注：02 §5.7 的 op 词表里没有“notContains / notStartsWith / notEndsWith”，所以 `!s.Contains("x")` 这类条件只保留 text、不产生 test，而不是用 notEquals 冒充。
  - 备注：`Condition.Text` 不截断（02 未给长度限制）；`Value.Text` 仍按 02 §5.4 截到 200 字符。
  - 备注：条件里的 `s.Equals("x")` 本身也是一个调用点，它会带上 `&&` 左侧的 logicalRight 条件——这是规格要求的结果，不是重复记录。
- 2026-10-23 04:10｜T11 DONE（11 passed，全量 137 passed；`index build` 冒烟输出 ok=true）｜BLOCKED：无｜新增待确认：Q-15｜继续 T12。
  - 备注：`ScreenFlowVersion.Rules` 原为 `"1.0"`，与 02 §7.4 明确的 `rulesVersion = "1"` 不一致，已改为 `"1"`。这是 T00 留下的笔误，不是规格冲突。
  - 备注（任务书产出清单外的改动）：`tests/ScreenFlow.Core.Tests` 增加对 `ScreenFlow.CSharp` 的 ProjectReference。T11 的解析测试要在 MiniPos 真实索引上跑，必须能构建索引；02 §1.1 的依赖规则约束的是产品工程，架构测试仍然只检查产品工程。
  - 备注：P5 的“内存索引”落在 `ResolutionIndex`（写在 `Resolution/Dispatcher.cs` 里，与 Dispatcher 同文件，避免超出任务书的产出清单）：overrides 反向表与类型层次各扫一遍 symbols 表建成，之后只查内存。
  - 备注：`IndexBuilder.Build` 是同步方法（内部 `BuildAsync(...).GetAwaiter().GetResult()`）。调用方是 CLI 命令、抽取是 CPU 密集，没有要让出的调用方；需要异步的调用方可以直接用 `BuildAsync`。
  - 备注：`index status` 的 stale 需要 `--project` 才能重算指纹；没给 `--project` 时返回 `staleChecked:false` 而不是谎报“新鲜”。
- 2026-10-23 05:30｜T12 DONE（7 passed，全量 144 passed）｜BLOCKED：无｜新增待确认：无｜继续 T13。
  - 实现结果：夹具上 screens=8（5 条已登记 + SCEW0001/SSAW0002/SSAW1113 三条 registered=0）、enum_members=18、domain_edges=12（navigate 10 + closePopup 2）。E-09/E-10/E-11 均按预期成立。
  - 备注：navigation 只对 `reachable=true` 的 callsite 产生边。不可达的调用仍留在 callsites 表里（02 §5.5），但它不是一次跳转——夹具里 `Legacy()` 中 return 之后的 `DisplayTargetScreen(ScreenID.SSAW0002)` 正是 E-10 要求“不得出现”的那条。
  - 备注：客面 TcpPresenter 用 `typeof(ScreenID).GetField(targetID)` 反射取画面（03 §10.3），4 处目标按规格记为 `screenIds: []` + `unresolvedText`，并各发一条 `I_NAVIGATION_SCREEN_UNRESOLVED`。这些会在 T18 变成断链记录，不是漏抽取。
  - 备注（任务书产出清单外的改动）：`tests/ScreenFlow.UnityPos.Tests` 增加对 `ScreenFlow.CSharp` 的 ProjectReference，理由同 T11 的 Core.Tests —— 适配器测试要在真实索引上跑。UnityPos 产品工程本身仍然不引用它，架构测试照旧守住这条线。
  - 备注：`AnchorResolver` 的 fileGlob 自带一个 glob→regex 实现（`**` 跨目录、`*` 不跨、`?` 单字符），不引入额外 NuGet 包。
  - 观察到一次偶发：`Symbols_Ids_StableAcrossRuns` 在“刚加完两个 ProjectReference 的那一次”全量运行中失败一次，随后单独跑与两次全量跑都通过（共 3 次复现尝试均通过）。已复核 rsp 选择（有完整的 platform→源文件数→路径 三级定序）、符号记录的并行写入（声明路径恒覆盖引用路径）、外部符号集合（与顺序无关），未找到不确定性来源。T20 的 `Determinism_TwoBuilds_ProduceIdenticalDump` 会给出更强的第二道信号；若再次出现将带着实际 diff 追查。
- 2026-10-23 06:40｜T13 DONE（6 passed，全量 150 passed）｜BLOCKED：无｜新增待确认：无｜继续 T14。
  - 实现结果：夹具上 texts=14、messages=28。YAML 行解析器覆盖 03 §15 的全部标量形态：双引号折行（`チャージ額を 選択して下さい`）、`\uXXXX` 转义、单引号 `''`、无引号、无引号折行、行尾反斜杠续行（不插空格）、空值（null 而不是空串）、未定义转义原样保留。
  - 备注：02 §2 把资源列为 P6、适配器列为 P7，而 `IDomainStage` 只有一个 Phase。做法是给 `IDomainAdapter` 加 `Phase`（默认 P7，ResourcesAdapter 覆盖为 P6），并给 `DomainStageContext` 加 `RanPhases`，让一个 stage 能如实报告它跑过的多个阶段——构建报告里 phases 现在是 P1..P7,P9，与 02 §2 一致。
  - 备注：`ResourceFiles.Enumerate` 是“哪些文件算资源”的唯一定义，构建期（写指纹）与 `index status`（重算指纹）共用。两处各写一份的话，每个索引都会被误判为 stale。
  - 备注：UXML 的“控件”判定为“带 name 属性的元素”——`ui:UXML` 与 `Style` 没有 name，自然被排除，而带 name 的 `ui:VisualElement`（画面根控件）必须留下。层级路径按 02 §9.9 只取祖先名，无名祖先用元素本地名，因此文档根 `UXML` 会出现在路径首段。
  - 确定性偶发问题的进展：抓到并修掉了一个真因 —— **部分方法**（`static partial void InitEdy`）在 Roslyn 里是两个符号部件，文档注释 ID 相同（同一条记录）但各自只知道自己那一半的声明位置；两棵树并行时谁后写入谁决定 `file`/`declarations`。现在统一归一到定义部件，并合并两部件的声明位置。修复后连续 13 次全量运行里仍出现过 1 次失败（无详细信息），故不能断言已根除；已把 `Symbols_Ids_StableAcrossRuns` 的断言改成一次性列出“哪个符号、两次分别是什么”，下次复现即可直接定位。T20 的 `Determinism_TwoBuilds_ProduceIdenticalDump` 会从导出侧再卡一道。
- 2026-10-23 08:00｜T14 DONE（4 passed，全量 154 passed）｜BLOCKED：无｜新增待确认：无｜继续 T15。
  - 实现结果：夹具上 controls=22、control_handlers=11、input_constraints=7。E-19（btnOther 的处理方法经 delegate 字段指向 lambda，certain）、E-20（Title=json:TextTitle certain；selectCharge_Other=json:ButtonTitle[2] candidate）、E-22（maxDigits→ERRB002/2、maxValue→ERRB022/22，跟代码不跟写反的注释）全部成立。
  - 修了一个真实的顺序缺陷：适配器之间靠领域表通信（02 §9），但 `DomainWriter` 原来只在整个 stage 结束时 flush 一次，于是 ui-bindings 读 `screens` 表时还是空的（controls=0）。改为**每个适配器跑完就 flush**。
  - 备注：同一约束出现在多个方法里会产生多行（02 §9.3 第 5 条明确要求不合并）。夹具里 `SetInputMinValue` 在 `CheckInputCharge` 与 OK 按钮 lambda 中各一次，因此 minValue 有 2 行、各带自己的 method_id。
  - 备注：`VelScreenMng` 这类“代码里 Query 得到、但不在本画面 UXML 里”的控件照常入表，只是 `hierarchy_path` 为空并发 `I_CONTROL_NOT_IN_UXML`；丢掉它就看不出代码与 UXML 对不上了。
  - 备注：控件在 `controls` 表里的主键是“被 Query 的名字”（02 §7.3 的 control_id）。同名控件在一个画面内靠层级路径区分，这一点在 UXML 索引里保留了；若将来真实工程出现同画面同名控件，control_id 需要改成“名字+路径”，届时在本机确认。
- 2026-10-23 09:10｜T15 DONE（4 passed，全量 158 passed，检查点要求的全量测试通过）｜BLOCKED：无｜新增待确认：无｜继续 T16。
  - 实现结果：夹具上 messageSend=10（全部 keys 已解析、keysComplete=true）、message_handlers=4。E-16（包装方法的 eventPath 经形参回溯得到 2 个 key，effectiveSites 指向真正决定取值的那个调用点）、E-17（被注释掉的请求不产生任何发送）、E-18（4 个接收方各自的 key 字面量，contains 与 equals 都保留）全部成立。
  - `EventPathMatcher` 放在 `ScreenFlow.Core/Graph`（按任务书要求），T18 遍历与 TCP 适配器共用同一套 02 §10.3 判定。
  - 备注（任务书产出清单外的改动，均为让 02 §9.1 第 4 条真正生效）：
    1. `ScreenIdValueProvider.TryResolve` 增加 `memberAccess(形参/局部的 ScreenID, Id)` 形态 —— T12 任务书第 4 条本就要求，当时夹具里没有用例所以没写；T15 的 `CustomerToOtherScreen(screenID, …)` 正是这个形态。
    2. `ValueResolver` 对 `memberAccess` 先问 provider、再走字段指向。否则 `screenID.Id` 会去追 `ScreenID.Id` 属性自己的赋值，一路走进 ScreenID 的构造函数（而对象创建不是 callsite，形参传播到那里就断了）。
  - 备注：`TcpReceiverAdapter` 的 adapter id 是 `tcp:receivers`，`AdapterOrder.IndexOf` 按 `:` 前的槽位排序，所以它紧跟在 tcp 发送之后、仍在 02 §2 的 tcp 槽位内。
- 2026-10-23 10:20｜T16 DONE（5 passed，全量 163 passed）｜BLOCKED：无｜新增待确认：无｜继续 T17。
  - 实现结果：keyseq_transitions=3（空表登记 0 行、被注释的条目不算）、di_registrations=8、keySeqDispatch=3（2 个解析出 psCode、1 个 null）、serviceResolve=6（其中 IDeviceBrokerMessageEvent 标 ambiguous）。E-12/E-13/E-14/E-15 全部成立。
  - 备注（任务书产出清单外的改动）：`ValueResolution` 增加 `Ambiguous` 字段——T16 任务书第 6 条点名要求，DI 一服务多实现时置 true，T18 据此记 MultipleDiRegistrations 断链。
  - 备注：`Di_ProviderNarrowsReceiver` 的断言改成“接收者的具体类型在有/无 provider 时的差别”。`IEValueRwCommand.ReadValue` 只有一个实现，候选集本来就只有一个，`Narrowed` 自然是 false——provider 的作用体现在“接收者解析出具体类型”，而不是“候选被削减”。
- 2026-10-23 11:40｜T17 DONE（3 passed，全量 166 passed）｜BLOCKED：无｜新增待确认：Q-16｜继续 T18。
  - 实现结果：boundaries=7（ExternalHardware ×2、ExternalMicroservice ×1、NativeInterop ×2、DatabaseMediated ×1、ReflectionConstruction ×1）、entries=14、domain_edges=43。E-24/E-25 全部成立。
  - 两处按规格字面修正：① `Type.GetType` 不在 02 §9.8 的反射入口清单里，去掉后反射边界正好 1 处（E-25 要求“1 处”）；② SQL 字面量也可能出现在**对象创建**的实参里（夹具 `new SQLiteCommand(sql, conn)`），不只是调用实参，已一并扫描。
  - 备注：原生边界对“创建 AndroidJavaObject”也记一条（key = Java 类名），对“在其上 Call”再记一条（key = 类名.方法名，E-25 要求的就是后者）。两条都是真实的跨界点。
- 2026-10-23 13:10｜T18 DONE（9 passed，全量 175 passed）｜BLOCKED：无｜新增待确认：无｜继续 T19。
  - 实现结果：夹具上 screen_edges=17、breaks=17（按类别：TerminalTcpCurrentScreen 7、DeferredExecution 2、ExternalHardware 2、KeySeqStageDependent 2、Unknown 2、DiAmbiguous 1、NativeInterop 1）。E-26 成立：0903→0907 恰好 2 条链，条件如期，且不含 Stage 条件。
  - 新增 `Graph/{ChainStep,ScreenEdge,TraversalState}.cs` 与 `Graph/ScreenGraphBuilder.cs`（P8 stage），CLI 把它接在 `UnityPosDomain` 之后：`new IndexBuilder(new IDomainStage[] { new UnityPosDomain(), new ScreenGraphBuilder() })`。
  - 修了一个真实缺陷：遍历时每个调用点的断链只写在该调用点的局部状态上，于是**同一方法中后续调用点看不到前面已经发生的断链**（0903→0907 经 Payment_SSAW0089 的链里丢了 `ExeKeyProc` 的 KeySeqStageDependent）。改为每个调用点用 `List<string> found` 收集本次新产生的断链 id，该调用点处理完后合并进方法内的游走状态——因为后一条语句本来就跑在一个索引无法确定的结果之上（02 §10.4）。
  - 备注：条件（`call.Conditions`）仍然是**逐调用点**附加、不跨调用点累积——守卫只约束它所守的那次调用；断链则相反，会向后传播。两者在 `Walk` 里分开处理。
  - 备注：`Build()` 的写出顺序必须是「先 `CheckInvariants()`、再写 edges、再写 breaks、最后统一 flush」。原先 `WriteAll` 排在 `Writer.Flush()` 之后，断链一条都没落库。
- 2026-10-23 14:05｜T19 DONE（12 passed，全量 187 passed）｜BLOCKED：无｜新增待确认：Q-17｜继续 T20。
  - 产出：`Core/Pathing/{ScreenGraph,Dijkstra,YenKShortest,PartialPathFinder,PathService}.cs` + `tests/ScreenFlow.Core.Tests/Pathing/{YenKShortestTests,PathServiceTests}.cs`。算法测试全部在**合成小图**上（菱形、平行边、环、不可达、maxHops、悬空边排序），E-31…E-34 在夹具上。
  - E-31 成立：0903→0907 第 1 跳 2 条平行边，overallConfidence=candidate，`triggerText.plain` 正是 `その他の金額を入力
(1,000円単位)`。E-32 成立：employee/SSAW0001 → employee/SSAW0089 → customer/SSAW0903 → customer/SSAW0907（跨终端）。E-33 成立：`Prefix("SSAW0903")` 唯一一条路径从 employee/SSAW0001 起。E-34 成立：0907→0281 无完整路径，partialPaths 全部止于悬空边且 `endsAtBreak` 非空。
  - 确定性：Dijkstra 松弛在代价相等时比较折叠边的最小 edgeId；Yen 的候选集按 (cost, 边数, edgeId 序列) 排序。`Yen_TieBreakDeterministic` 把同一组边正序/逆序各建一次图，要求两次结果的签名序列完全相同。
  - overallConfidence 的定义（02 §11.4 未写死）：一跳只要有**一条** certain 且无断链的平行边就算这一跳 certain，整条路径所有跳都 certain 才是 certain。这样既不会把单条 candidate 边说成 certain，也不会因为列出了一条 candidate 平行边就把本来确定的路径降级。
  - 备注：`PathService` 读 `screens.standbyScreens` 与 `resources.stringTableSearchOrder/defaultLocale` 都是从 `DomainConfig` 的 `JsonElement` 里取 —— Core 不能引用 UnityPos，这两项配置又是领域知识，只能按 JSON 路径读。
- 2026-10-23 10:40｜T20 DONE（6 passed，全量 193 passed）｜BLOCKED：无｜新增待确认：无（Q-17 已定案并回写）｜继续 T21。
  - 产出：`Core/Query/{ScreenQuery,BreakQuery,SymbolQuery,EvidenceQuery,TextQuery,MessageQuery,DebugQuery}.cs`、`Core/Storage/SnippetHasher.cs`、`tests/ScreenFlow.Cli.Tests/QueryCommandsTests.cs`；CLI 增加 28 个选项与全部查询命令。
  - `evidence.snippet_hash` 注入：T03 只留了 `IrWriter` 的 `snippetHasher` 钩子，T11 没接。现在 `IndexBuilder` 传入一个在 flush 时（files 已落库）按 `files.encoding` 解码片段并 SHA-256 的 hasher；为此把 `IrWriter` 的哈希从 Write 时推迟到 `InsertEvidence`。夹具 1068 行 evidence 全部有值。
  - `snippet_hash` 的片段定义为 span 的精确文本（1-based 行列、end 排他），列按 UTF-16 单元计，与 Roslyn 一致；行分隔识别 CR/LF/CRLF。
  - Q-17 定案：CLI 对 `screen get/controls/constraints/transitions` 与 `path find` 的 from/to 一律「单终端→单对象、多终端→数组」。
  - 备注：`debug callsites` 的 creation 范围查询要 join `symbols` 取 file（creations 表没有 file 列）；`break list --screen/--edge` 通过 `screen_edges.ends_at_break` 与 `json.breakIds` 反查，不反规范化 breaks 表。
  - 备注：`message get --code` 的 `ERRB022` 经 `enum_members.name` 解析成 22；数字码直接解析，name 反向从 enum_members 补全。
- 2026-10-23 11:05｜T21 DONE（28 passed）｜BLOCKED：无｜新增待确认：无｜继续 T22。
  - 产出：`python/pyproject.toml`（`screenflow-mcp`，依赖 `mcp>=1.2,<2`，dev 依赖 `pytest>=8`）、`python/screenflow_mcp/{__init__,__main__,server,cli_runner,schemas,truncate}.py`、`python/tests/{fake_cli,test_runner,test_tools,test_truncate,test_selftest}.py`。
  - `cli_runner`：`SCREENFLOW_CLI` 以 `.dll` 结尾前置 `dotnet`、以 `.py` 结尾前置 `sys.executable`；自动追加 `--db <SCREENFLOW_DB_DIR>/<profile>.db --profile <profile>`；`evidence get` 追加 `--project`；退出码非 0 且 stdout 合法信封→原样返回，stdout 非法→`E_CLI_FAILURE`（stderr 末尾 2000 字符），超时→`E_CLI_FAILURE`。
  - `truncate`：超过 60,000 字符时找 `data` 中最长数组、逐个弹掉末尾元素，设 `meta.truncatedByMcp=true` 与 `meta.truncatedPath`。
  - 备注：`mcp` 版本实际解析到 1.30.0（符合 `>=1.2,<2`）。`--selftest` 用 fake_cli 验证入口与 4 步流程。
- 2026-10-23 11:20｜T22 DONE（4 passed，Python 全量 32 passed）｜BLOCKED：无｜新增待确认：无｜继续 T23。
  - 产出：`deploy/.github/agents/screen-flow.agent.md`、`deploy/.github/skills/screen-flow/SKILL.md`、`deploy/.vscode/mcp.json`、`deploy/README.md`、`python/tests/test_deploy_files.py`。
  - agent.md 的 frontmatter 逐字采用 04 §3.1；正文含 §3.2 全部 7 条规则、§3.3 的 8 步调用顺序、§4 报告模板（7 个章节标题 + 表格）。
  - SKILL.md frontmatter 逐字采用 04 §5；15 个工具各一节；术语表覆盖 02 §8.3 的 9 个类别与 13 个原因码。
  - mcp.json 逐字采用 04 §6；README 含发布/安装/构建索引/复制文件/验证 5 步 + 索引构建命令示例 + 常见问题（索引不存在、derived、stale、MCP 未启动、E_CLI_FAILURE）。
- 2026-10-23 11:30｜T23 DONE（3 passed，Python 全量 35 passed）｜BLOCKED：无｜新增待确认：无｜全部任务 DONE。
  - 产出：`acceptance/{run_acceptance,check_report,strip_comments}.py`、`acceptance/expectations.{pos,minipos}.json`、`acceptance/README.md`、`python/tests/test_acceptance_scripts.py`。
  - `strip_comments`：删除 `//` 行注释与 `/* */` 块注释，并跳过字符串/字符/逐字/插值字符串（内容置空、保留换行，行号不变）。
  - 断言 DSL：`exists/equals/contains/count>=/allNot` + `and/or/not`；JSON 路径支持点号 + `[N]`/`[*]`。
  - 交叉核对 A-11…A-16 在脚本实现：去注释正则计数比对 `debug stats/callsites/sends`；`#if TeamUI` 区间定位支持嵌套 `#if`。MiniPos 实测：A-11 N1=12/N2=10（差值 2 为 TcpModel 两个方法定义行）、A-13 M1=M2=3、A-14 D1=D2=8 且注册表 5/registered=5、A-15 TeamUI 49-52 空、A-16 52 空。
  - `psutil` 已检测未安装 → 内存项按 02 §1.2 不自行安装，记 WARN 处理路径已留。
  - 修复 T20 遗留：`debug sends` 的 SendView 增加 `type/method/file/line`（发送方类型=方法所在类型名），A-07 的 `type=Payment_SSAW0089_Presenter` 断言据此成立。
