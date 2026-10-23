# MiniPos —— 仿真工程

按 `docs/03_源码事实手册.md` 的真实形态仿造的迷你 Unity 工程，是本仓库**唯一**可用于冒烟测试的被解析对象
（CLAUDE.md §2：真实 POS 源码不在生成代码的机器上）。

- 规格：`docs/05_测试与验证规范.md` §3
- 形态依据：`docs/03_源码事实手册.md` §3–§13、§17–§21
- 编译验证：`tests/ScreenFlow.CSharp.Tests/Fixture/FixtureCompilesTest.cs`（Editor / Android 两个配置各断言 error = 0，含 CP932 文件）
- 资源验证：`tests/ScreenFlow.CSharp.Tests/Fixture/FixtureResourcesTest.cs`
- CP932 生成：`python tools/make-cp932.py`（源文件 `tools/KEY01.src.txt` 为 UTF-8，便于审阅）

## 规模

| 项目 | 数量 |
|---|---|
| `.cs` 文件 | 66（65 个 UTF-8 + 1 个 CP932：`Assets/Common/Legacy/KEY01_KeyCalculator.cs`） |
| rsp | `ed01.dag`（Editor）、`an01.dag`（Android）各 1 个主 rsp + `common.rsp`；另有 1 个 `mvfrm` 干扰文件 |
| 画面 | employee：SSAW0001 / SSAW0089 / SSAW0281；customer：SSAW0903 / SSAW0907；未登记：SSAW0002 / SCEW0001 |
| 资源 | UXML 2、画面初始化 JSON 3、本地化 YAML 5、错误文案 JSON 1 |

## 刻意埋入的「不得出现」形态

这些是反编造规则的底线测试 —— 解析结果里出现任何一个都是缺陷：

| 形态 | 位置 |
|---|---|
| `#if TeamUI` 块里的 `DisplayTargetScreen(ScreenID.SSAW0002)` | `SSAW0001_Presenter.OnDisplay` |
| 被注释掉的 `DisplayTargetScreen(ScreenID.SSAW0002)` | 同上 |
| `return` 之后不可达的 `DisplayTargetScreen(ScreenID.SSAW0002)` | `EdyCharge_SSAW0281_Presenter.Legacy`（同方法含 `goto`） |
| 被注释掉的 `SocketRequestData`（`TargetID = SSAW0907`） | `Payment_SSAW0089_Presenter.OnClickBtnEdy` |
| 被注释掉的键序条目 `PS_CCLOSE` | `NmlKty.Edy.cs` |
| 把 Err2 / Err22 写反的两行注释 | `EdyCharge_SSAW0907_View.SetKeyPadInput` |

## 期望事实对应表（E-01 … E-38，见 05 §4）

| E | 主要夹具文件 | 说明 |
|---|---|---|
| E-01 | `Library/Bee/artifacts/ed01.dag/{Assembly-CSharp.rsp, common.rsp, Assembly-CSharp.mvfrm.rsp}` | 主 rsp 用 `@` 嵌套引用 `common.rsp`；`mvfrm` 必须被排除 |
| E-02 | `ed01.dag/Assembly-CSharp.rsp` 的 `-r:"Library/Missing/Nothing.dll"`；`Assets/Common/Legacy/KEY01_KeyCalculator.cs`（T05） | 缺失引用与非 UTF-8 文件各一处 |
| E-03 | `Library/Bee/artifacts/an01.dag/Assembly-CSharp.rsp` | 定义 `UNITY_ANDROID`、无 `UNITY_EDITOR` |
| E-04 | 全部 `Assets/**/*.cs` | 由 `FixtureCompilesTest` 断言 |
| E-05 | `Assets/Common/View/Script/Utilities_s3/ScreenID.cs`、`Assets/ScenePosRegister/CEMenu/Common/View/Script/ScreenID.CEMenu.cs` | 同一 partial 类型的两个声明 |
| E-06 | `Assets/ScenePosRegister/Misc/KsPosaA.cs`、`Assets/ScenePosRegister/Misc2/KsPosaB.cs` | 两个不同命名空间下的同名类 `KsPosa`；SSAW0001 只调用 `Misc.KsPosa` |
| E-07 | `Assets/Common/Legacy/KEY01_KeyCalculator.cs`（T05） | CP932 字节，含日文字面量 |
| E-08 | 全部 `.cs`（每个文件首尾各一处 `//▼ ADD` / `//▲ ADD`） | 变更标记抽取 |
| E-09 | `Scene_PosRegister_View.cs`、`Scene_Customer_View.cs`、`ScreenID.cs`、`ScreenID.CEMenu.cs` | 登记表决定终端；SSAW0002 / SCEW0001 未登记 |
| E-10 | `SSAW0001_Presenter.cs`（TeamUI 块 + 注释行）、`EdyCharge_SSAW0281_Presenter.Legacy`（不可达） | 三处来源都不得产生 navigate 边 |
| E-11 | `SSAW0001_Presenter.OnDisplay` 的 `#if UNITY_EDITOR` 块 | 只在 Editor 配置出现 |
| E-12 | `Assets/ScenePosRegister/Common/Model/Script/GaplKey/NmlKty.Edy.cs` | 3 条有效条目 + 1 条被注释 + 1 张空表 |
| E-13 | `Payment_SSAW0089_Presenter.OnClickBtnOther` | `ExeKeyProc` 的 PsCode = `PS_CEDY_CHARGEOTHER` |
| E-14 | `EdyCharge_SSAW0281_Presenter.OnClickBtnOther` | `ExeKeyProc(e.KeyRequest)`，PsCode 不可解析 |
| E-15 | `Assets/PosModule/Common/ServiceLocator/ServiceLocator.DeviceBroker.cs` | 工厂别名登记 + `IDeviceBrokerMessageEvent` 两处登记 |
| E-16 | `Payment_SSAW0089_Presenter`（两处发送）、`EdyCharge_SSAW0281_Presenter.CustomerToOtherScreen`（形参传播 + 条件拼接） | 事件路径的三种形态 |
| E-17 | `Payment_SSAW0089_Presenter.OnClickBtnEdy` 的注释行 | 被注释的对象创建不得产生记录 |
| E-18 | `EdyCharge_SSAW0907_Presenter.OnReceiveSocket`、`Payment_SSAW0089_Presenter.OnReceiveSocket`、`EdyCharge_SSAW0281_Presenter.OnReceiveSocket` | `Contains` 与 `Equals` 两种判断 |
| E-19 | `EdyCharge_SSAW0903_View.SetEvent`（委托字段形态）、`EdyCharge_SSAW0903_Presenter.OnInstanced`（`eventHandler = this`） | 必须穿透委托字段与接口字段 |
| E-20 | `EdyCharge_SSAW0903_View.{GetScreenControl, SetView}`、`Assets/Common/View/Resources/ScreenInitialize/EdyCharge_SSAW0903_Initialize.json`（T05） | `listButtons` 的 Add 顺序决定 `ButtonTitle[2]` → `btnOther` |
| E-21 | `Assets/Localization/StringTable/Customer/*.asset`（T05） | YAML 的四种标量形态 |
| E-22 | `EdyCharge_SSAW0907_View.{SetKeyPadInput, CheckInputCharge, SetEvent}` | 四个约束各自的方法与条件；注释写反 |
| E-23 | `Assets/StreamingAssets/ErrMsg/ErrMsgType1_Initialize.json`（T05）、`Common_Application_Model.cs` | 错误文案与取得函数 |
| E-24 | `Assets/PosModule/DeviceBroker/`、`Payment_SSAW0089_Presenter.{OnClickBtnEdy, OnEdyResponse}`、`EdyCharge_Model.GaEntryChargeOther` | 硬件边界与 MQTT 回流候选 |
| E-25 | `Payment_SSAW0089_Presenter.OnClickBtnAmountOK`（API）、`SSAW0001_Presenter.{OnDisplay, LoadItems}`（原生 + SQL 读）、`ItemWriter.Save`（SQL 写）、`EdyCharge_SSAW0281_Presenter.OnClickBtnDebug`（反射 + DI） | 四类其他边界 |
| E-26 | `EdyCharge_SSAW0903_Presenter.OnClickBtnOther` → `Payment_SSAW0089_Presenter` / `EdyCharge_SSAW0281_Presenter` | 同一条画面边的两条链 |
| E-27 | 同 E-26 的链 | TerminalTcpCurrentScreen / DeferredExecution / KeySeqStageDependent |
| E-28 | `Payment_SSAW0089_Presenter.OnClickBtnEdy`（远程显示）、`SSAW0001_Presenter.OnClickBtnPay`（本地跳转） | 远程显示边与本地确定边 |
| E-29 | `Payment_SSAW0089_Presenter.SendUnknown`（`PosRegister.Nobody.Handles`） | 无接收方 → 悬空边 |
| E-30 | `EdyCharge_SSAW0281_Presenter.OnClickBtnOther` | 不可解析的 `ExeKeyProc` 不终止整条链 |
| E-31 | 同 E-26 + `EdyCharge_SSAW0903_Initialize.json`（T05） | `path find` 的第 1 跳含 2 条平行边 |
| E-32 | `SSAW0001_Presenter` → `Payment_SSAW0089_Presenter` → 客面 | 跨终端 4 画面路径 |
| E-33 | `ScreenID.SSAW0001` + `config/unitypos.json` 的 `standbyScreens` | 前置路径起点 |
| E-34 | 客面 SSAW0907 无通往员工面 SSAW0281 的边 | 不可达的诊断 |
| E-35 | 无专属夹具文件 —— 对**整个夹具**构建两次比较导出哈希 | 确定性，T20 的检查点 |
| E-36 | 无专属夹具文件 —— 对夹具产出的全部事实做不变量检查 | 每个事实都有 evidence |
| E-37 | 无专属夹具文件 —— 同上 | 有断链 ⇒ candidate |
| E-38 | 无夹具文件 —— 检查的是 harness 自身的程序集引用 | `tests/ScreenFlow.Architecture.Tests` |

> E-35 ～ E-38 是对 harness 自身输出的不变量检查，不对应某一个夹具源文件；这里照 T04 的要求逐条登记，并写明它们
> 为什么没有专属文件，而不是硬凑一个对应关系。

## 与真实工程的已知差异

| 差异 | 原因 |
|---|---|
| `UniTask` → `System.Threading.Tasks.Task` | 05 §3.1 指定；签名其余部分不变 |
| 外部库用 `Assets/Stubs/` 下的源码桩 | 05 §3.1；命名空间与真实库一致，只写编译所需成员 |
| 方法体精简 | 05 §3.1 允许；**调用形态不得改写**，故委托字段订阅、`?.`、目标类型 `new()`、集合/对象初始化器、条件拼接、`nameof`、`ref` 委托全部保留 |
| 画面数量远少于真实工程 | 05 §3.1：体量小，但每种机制与每类断链至少出现一次 |
