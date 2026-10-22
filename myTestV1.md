# myTestV1 — Screen-Path Analysis Harness

Detailed design specification (v1).
Audience: an LLM or engineer generating the implementation from this document.
Language of implementation: C# (.NET 8) core + extractors, Python calling layer.

---

## 1. Purpose

Given a **start screen ID (A)** and an **end screen ID (Z)** of the SEJ next-generation POS Unity application, the harness returns, without executing the application:

1. every screen traversed from A to Z (up to K candidate paths);
2. for each hop, the full method chain (namespace, type, method, source file, line range);
3. every API call, device/hardware interaction and external data entry point on that chain;
4. the **prefix path** from the main standby screen to A;
5. the UI facts needed to act on each hop (button display text in Japanese, input constraints, error codes);
6. **every broken link: where it breaks, why it breaks, what crosses the boundary, and where control re-enters the analysable code.**

The target application integrates with hardware terminals, microservices and native plugins whose source code is unavailable. The harness must never guess across those boundaries. It must report the boundary precisely.

The harness is consumed as a **function-calling tool by an LLM agent**. All expensive analysis happens offline in the index; queries are cheap and return structured JSON.

---

## 2. Scope of v1

| Item | Decision |
|---|---|
| Core engine language | C# (.NET 8) |
| Source parsing | Pluggable extractors. v1 ships the C#/Roslyn extractor only |
| Python | Thin calling layer (subprocess wrapper + function-calling tool schemas) |
| Compilation input | Unity-generated compile inputs (`Library/Bee/artifacts/*.dag/*.rsp`), provided by the user |
| Build profiles | Two, switchable: `android-onsite` and `windows-editor` |
| Index scope | Full project index (all assemblies, all screens) |
| Path output | K candidate paths, K configurable |
| Prefix path | Included in v1 (from main standby screens to A) |
| UI layer | Included in v1: button text (ja), input constraints, error codes |
| Break links | 8 named categories, with resume-point reconnection |
| Incremental update | Not in v1. Full rebuild only |
| Delivery surface | CLI (JSON stdout) + Python library. MCP server is a later phase |
| Runtime tracing | Not in v1 |

### 2.1 Explicit non-goals

- Proving which branch executes at runtime. The harness distinguishes *certain* from *candidate* and never collapses candidates.
- Analysing the behaviour of external systems (hardware firmware, microservice internals, native plugins).
- Deriving specification intent. The harness reports what the code does, not what it should do.
- Whole-program alias analysis and async interleaving proofs.

---

## 3. Target system facts the implementation must honour

These are verified properties of the analysed repository. They drive the design and must not be re-litigated during implementation.

| Fact | Consequence for implementation |
|---|---|
| ~9,400 C# files, ~2.4M lines, Unity 2022.3.68f1, C# 9 | Index build is a long batch job; must stream and report progress |
| No `.sln`/`.csproj` in repo | Compilation input comes from Unity `.rsp` response files |
| Single APK, two terminals (employee / customer), separate scenes, linked by TCP | Screen graph nodes are keyed by `(terminal, screenId)` |
| 3 scenes: `SceneLoading`, `ScenePosRegister` (employee), `SceneCustomer` (customer) | Screen registries are per-scene |
| ~32% of lines are comments; ~94k lines of commented-out old code; comments are sometimes factually wrong | **Comments are never a fact source.** Only `▼ADD/▼MOD/▼DEL` markers may be retained as metadata |
| `TeamUI` / `TeamUIDummy` are never defined in real configurations | Must be excluded by preprocessor symbols, not by text filtering |
| 3 files are Shift-JIS, rest UTF-8 (some with BOM); 75 files mix CRLF/LF | Encoding detection with CP932 fallback is mandatory |
| 125 groups of duplicate simple type names across directories | **Never resolve symbols by short name** |
| 819 `partial` types (`ServiceLocator`, `NmlKty`, `StageNo`...) | Type-level facts must be merged across files |
| 3,163 `goto` statements in 183 files | Intra-method control flow must use Roslyn `ControlFlowGraph`, never textual inference |
| `StageNo`, `PsCode`, `ResultStatusCode` are `Enumeration` subclasses, not C# enums | Constant resolution must handle static readonly field members, not just enum literals |
| Key relationships are established by strings, registries, reflection, queues, static state and "the current screen" | Call-graph alone is insufficient; domain adapters are mandatory (§8) |

### 3.1 Key file anchors (relative to the Unity project root)

| Concern | Path |
|---|---|
| Unity version | `ProjectSettings/ProjectVersion.txt` |
| Compile input | `Library/Bee/artifacts/*.dag/*.rsp` |
| Screen IDs | `Assets/Common/View/Script/Utilities_s3/ScreenID.cs`, `Assets/ScenePosRegister/CEMenu/Common/View/Script/ScreenID.CEMenu.cs` |
| Employee screen registry | `Assets/ScenePosRegister/Common/View/Script/Scene_PosRegister_View.cs` (`InitDictionary`) |
| Customer screen registry | `Assets/SceneCustomer/Common/View/Script/Scene_Customer_View.cs` |
| Screen base classes | `Assets/Common/Script/Base/BaseScreen.cs`, `BaseSceneView.cs`, `BaseScreenManager.cs`, `ScreenManager.cs`, `Enumeration.cs` |
| DI | `Assets/PosModule/Common/ServiceLocator/ServiceLocator*.cs` |
| Key sequence | `Assets/PosModule/Apl/KeySeq/` (`IKeySeqEventDispatcher`, `IKeySeqRegister`, `IStageNoStatus`), `Assets/ScenePosRegister/Common/Model/Script/GaplKey/NmlKty*.cs` |
| Stage / event codes | `Assets/PosModule/Apl/KeySeq/Const/StageNo.cs`, `PsCode.cs` |
| TCP | `Assets/Common/Model/Script/Tcp/`, `Assets/ScenePosRegister/Common/Presenter/Script/TcpPresenter.cs`, `Assets/SceneCustomer/Common/Presenter/Script/Tcp/TcpPresenter.cs` |
| Devices | `Assets/PosModule/DeviceBroker/` (`IScannerCommand`, `IEValueRwCommand`, `ICashChangerCommand`, `IPrinterCommand`, `IEMVRwCommand`, ...), `Assets/Common/Presenter/Script/DeviceBroker/` |
| Model queue | `Assets/Common/Script/Queueing/` |
| API | `Assets/Common/Script/API/`, `Assets/StreamingAssets/MSLocal/` |
| Screen initialize JSON | `Assets/Common/View/Resources/ScreenInitialize/` |
| Localization | `Assets/Localization/StringTable/` |
| UXML | `Assets/**/*.uxml` |

---

## 4. Architecture

```text
┌──────────────────────────────────────────────────────────────────┐
│ Python calling layer (myTestV1/python)                           │
│   harness_client: subprocess wrapper, dataclasses,               │
│   function-calling tool schemas. No analysis logic.              │
└──────────────────────────────────────────────────────────────────┘
                      │ CLI, JSON over stdout
                      ▼
┌──────────────────────────────────────────────────────────────────┐
│ Harness.Cli (.NET 8)                                             │
│   argument parsing, envelope formatting, exit codes              │
└──────────────────────────────────────────────────────────────────┘
                      ▼
┌──────────────────────────────────────────────────────────────────┐
│ Harness.Core  — NO reference to Roslyn, NO reference to Unity    │
│   • IR model + schema validation                                 │
│   • Index store (SQLite)                                         │
│   • Graph construction, K-path search, prefix path               │
│   • Break-link detection and resume resolution                   │
│   • Evidence and confidence model                                │
│   • Query services                                               │
└──────────────────────────────────────────────────────────────────┘
       ▲ consumes IR only                   ▲ consumes IR + resources
       │                                     │
┌──────────────────────┐          ┌──────────────────────────────┐
│ Harness.Extractors.  │          │ Harness.Domain.UnityPos      │
│ CSharpRoslyn         │          │  screen registry, keyseq,    │
│  Roslyn → IR facts   │          │  TCP, DI, device, queue,     │
│                      │          │  SQL, UXML, localization     │
└──────────────────────┘          └──────────────────────────────┘
       ▲
       │ (future) Harness.Extractors.Java, .Python — any process
       │ that emits IR JSON conforming to the schema
```

### 4.1 The cross-language reuse rule (non-negotiable)

- `Harness.Core` **must not** reference `Microsoft.CodeAnalysis.*` or any language-specific library. A compile-time check (§17.3) enforces this.
- The only contract between a language and the core is the **IR JSON schema** (§6).
- Adding Java or Python support later means writing a new extractor process that emits conforming IR. The graph, path search, break detection and query layers are reused unchanged.
- Domain adapters (§8) consume IR, not Roslyn. They are therefore *project-specific* but *language-agnostic in mechanism*: the same adapter pattern applies to another codebase.

### 4.2 Extractor contract

```csharp
namespace Harness.Core.Extraction;

public interface IFactExtractor
{
    string ExtractorId { get; }          // e.g. "csharp-roslyn"
    string IrSchemaVersion { get; }      // must match Core's supported version
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct);
}

public sealed record ExtractionRequest(
    string ProjectRoot,
    ProfileDefinition Profile,
    IProgress<ExtractionProgress> Progress);

public sealed record ExtractionResult(
    IAsyncEnumerable<IrRecord> Records,   // streamed, see §6.1
    IReadOnlyList<Diagnostic> Diagnostics);
```

An external-process extractor adapter is also provided so non-.NET extractors can be plugged in:

```csharp
public sealed class ExternalProcessExtractor : IFactExtractor
{
    // Spawns a configured command, reads newline-delimited IR JSON from stdout,
    // validates each record against the schema, fails the build on invalid records.
}
```

---

## 5. Repository layout

```text
myTestV1/
├─ DESIGN.md                       (this document)
├─ README.md
├─ harness.sln
├─ config/
│  ├─ profiles.json                build profile definitions (§7.2)
│  └─ domain.unitypos.json         domain adapter anchors (§8.10)
├─ src/
│  ├─ Harness.Core/
│  │  ├─ Ir/                       IR records, schema, validator
│  │  ├─ Storage/                  SQLite store, DDL, migrations
│  │  ├─ Graph/                    graph builder, screen graph
│  │  ├─ Pathing/                  K-shortest path, prefix path
│  │  ├─ Breaks/                   break detection, resume resolution
│  │  ├─ Query/                    query services (one per CLI verb)
│  │  ├─ Evidence/                 evidence records, snippet hashing
│  │  └─ Extraction/               IFactExtractor, ExternalProcessExtractor
│  ├─ Harness.Extractors.CSharpRoslyn/
│  │  ├─ Compilation/              rsp parsing, workspace construction
│  │  ├─ Walkers/                  syntax/semantic walkers
│  │  └─ Emit/                     IR emission
│  ├─ Harness.Domain.UnityPos/
│  │  ├─ Screens/                  registries, navigation
│  │  ├─ KeySeq/                   stage tables
│  │  ├─ Transport/                TCP, MQTT, API
│  │  ├─ Di/                       ServiceLocator
│  │  ├─ Data/                     SQL tables
│  │  └─ Ui/                       UXML, ScreenInitialize JSON, localization
│  └─ Harness.Cli/
├─ python/
│  ├─ pyproject.toml
│  └─ harness_client/
│     ├─ __init__.py
│     ├─ client.py                 subprocess wrapper
│     ├─ models.py                 dataclasses mirroring the JSON envelope
│     └─ tools.py                  function-calling tool schemas
├─ schema/
│  ├─ ir-1.0.schema.json
│  └─ envelope-1.0.schema.json
└─ tests/
   ├─ Harness.Core.Tests/
   ├─ Harness.Extractors.CSharpRoslyn.Tests/
   ├─ Harness.Domain.UnityPos.Tests/
   └─ fixtures/                    small synthetic Unity-like projects
```

---

## 6. Intermediate Representation (IR)

The IR is the language-neutral contract. Schema version `1.0`. Serialised as **newline-delimited JSON** (one record per line) to allow streaming of millions of records.

### 6.1 Record envelope

```json
{ "k": "node|edge|evidence|diag|meta", "v": { } }
```

### 6.2 Node record

| Field | Type | Notes |
|---|---|---|
| `id` | string | Stable ID, §6.5 |
| `kind` | enum | `assembly`, `file`, `namespace`, `type`, `method`, `field`, `property`, `parameter`, `local`, `literal`, `externalBoundary` |
| `name` | string | Simple name |
| `displayName` | string | Fully qualified, with signature for methods |
| `namespace` | string? | |
| `declaringType` | string? | Node id |
| `assembly` | string | Assembly name |
| `file` | string? | Repo-relative, forward slashes |
| `span` | span? | `{startLine,startCol,endLine,endCol}`, 1-based |
| `modifiers` | string[] | `static`, `abstract`, `partial`, `async`, `override`, `virtual` |
| `returnType` | string? | For methods/properties |
| `parameters` | object[]? | `{name,type,refKind}` |
| `attributes` | object[]? | `{type, arguments[]}` |
| `baseTypes` | string[]? | Node ids or external type names |
| `isCompilerCertain` | bool | False if the symbol could not be fully bound |

### 6.3 Edge record

| Field | Type | Notes |
|---|---|---|
| `id` | string | Deterministic hash of `(kind, from, to, evidence[0])` |
| `kind` | enum | See table below |
| `from` | string | Node id |
| `to` | string | Node id, or `external:*` |
| `confidence` | enum | `certain`, `candidate`, `unresolved` |
| `candidateGroup` | string? | Shared by all candidates of one unresolved dispatch |
| `deferred` | bool | True if execution is queued/async, not inline |
| `conditions` | object[] | `{kind, expression, evidenceId}` — guards governing this edge |
| `arguments` | object[]? | Resolved constant arguments, §6.4 |
| `evidence` | string[] | Evidence ids |

Edge kinds emitted by a language extractor (generic):

`calls`, `overrides`, `implements`, `inherits`, `reads`, `writes`, `creates`, `returns`, `throws`, `subscribes`, `unsubscribes`, `passes`, `typeOf`, `containedIn`, `branchesTo`

Edge kinds emitted by domain adapters (project-specific):

`registersScreen`, `navigatesTo`, `registersService`, `resolvesService`, `registersKeySeq`, `dispatchesKeySeq`, `sendsSocket`, `receivesSocket`, `sendsDeviceCommand`, `receivesDeviceEvent`, `callsApi`, `readsTable`, `writesTable`, `enqueues`, `bindsControl`, `constrainsInput`, `producesMessage`, `crossesBoundary`

### 6.4 Resolved argument

Critical for this project: registries and dispatch tables are built from literals and `typeof`.

```json
{
  "index": 0,
  "kind": "constant|typeOf|enumerationMember|unresolved",
  "type": "System.String",
  "value": "Customer.NextDispReq.SSAW0903",
  "targetNodeId": null
}
```

- `constant`: compile-time constant, including concatenation of constants and `nameof`.
- `typeOf`: `targetNodeId` points at the type node.
- `enumerationMember`: a `static readonly` member of an `Enumeration` subclass (`StageNo.X`, `PsCode.Y`, `ScreenID.SSAW0903`); `value` carries the member name, `targetNodeId` the field node.
- `unresolved`: anything else. **Never approximate.**

### 6.5 Stable identifiers

```text
nodeId = sha1( profileId + "|" + assemblyName + "|" + symbolKey )[0..20]
```

- `symbolKey` for a C# symbol is `ISymbol.GetDocumentationCommentId()`.
- For symbols without a doc-comment id (lambdas, local functions, locals), append an ordinal path:
  `M:Ns.Type.Method(...)~lambda[2]~local[0]`.
- Files use `F:<repo-relative-path>`. External boundaries use `X:<category>:<contractKey>`.
- IDs are deterministic across runs with the same input. Hash truncation collisions must be detected at insert time and reported as a fatal diagnostic.

### 6.6 Evidence record

```json
{
  "id": "e_8f3a...",
  "file": "Assets/SceneCustomer/.../EdyCharge_SSAW0907_Presenter.cs",
  "span": { "startLine": 412, "startCol": 13, "endLine": 412, "endCol": 61 },
  "snippetHash": "sha256:...",
  "snippet": null
}
```

`snippet` is `null` by default and populated only by the `evidence get` command. This keeps query payloads small.

---

## 7. C# extractor — compilation loading

### 7.1 Input discovery

1. Read `ProjectSettings/ProjectVersion.txt`; record declared Unity version.
2. Locate `Library/Bee/artifacts/*.dag/`; enumerate `*.rsp`.
3. Exclude response files that are not compilation inputs (e.g. names containing `.mvfrm.`).
4. For each `.rsp`, parse:
   - one argument per line; strip surrounding quotes; handle `@nested.rsp` recursively;
   - `-define:SYMBOL` (repeatable), `-r:<path>` / `-reference:`, `-out:<path>`, `-langversion:`, `-nullable:`, `-unsafe`, `-analyzer:`, `-ruleset:`;
   - remaining non-flag lines ending in `.cs` are source files;
   - paths are resolved relative to the Unity project root.
5. Record, per assembly: source file list, reference list, define list, language version.
6. **Preflight diagnostics** (always emitted, never silently ignored):
   - declared Unity version vs the version that produced the cache;
   - references that do not exist on disk;
   - source files listed in `.rsp` but missing on disk, and `.cs` files on disk absent from every `.rsp`;
   - files whose encoding is not UTF-8.

If no usable `.rsp` set is found, the build **fails** with `E_NO_COMPILATION_INPUT` and instructs the user to open the project in Unity and compile. v1 does not fabricate a compilation.

### 7.2 Profiles

`config/profiles.json`:

```json
{
  "schemaVersion": "1.0",
  "profiles": [
    {
      "id": "android-onsite",
      "label": "Android on-site build",
      "rspSource": { "mode": "unityCache", "dagGlob": "Library/Bee/artifacts/*.dag" },
      "defineOverrides": {
        "add":    ["UNITY_ANDROID", "UNITY_64"],
        "remove": ["UNITY_EDITOR", "UNITY_EDITOR_WIN", "UNITY_STANDALONE_WIN",
                   "UNITY_INCLUDE_TESTS", "TeamUI", "TeamUIDummy", "APP_TEST"]
      },
      "assemblyFilter": { "exclude": ["*-Editor", "*Tests*"] }
    },
    {
      "id": "windows-editor",
      "label": "Windows Editor build",
      "rspSource": { "mode": "unityCache", "dagGlob": "Library/Bee/artifacts/*.dag" },
      "defineOverrides": { "add": [], "remove": ["TeamUI", "TeamUIDummy"] },
      "assemblyFilter": { "exclude": [] }
    }
  ]
}
```

Rules:

- If the Unity cache was produced for a different platform than the profile declares, the profile is marked `derivation: "derived"` in `snapshots` and a diagnostic is emitted. The index is still built, but every query result carries this flag in `meta`.
- `TeamUI` and `TeamUIDummy` are removed in **both** profiles; they are never defined in any real configuration.
- A profile is an index dimension: node and edge IDs include `profileId`, so two profiles coexist in one database without collision.

### 7.3 Compilation construction

```csharp
var parseOptions = new CSharpParseOptions(
    languageVersion: LanguageVersion.CSharp9,
    documentationMode: DocumentationMode.None,
    preprocessorSymbols: profileDefines);

var compilationOptions = new CSharpCompilationOptions(
    OutputKind.DynamicallyLinkedLibrary,
    allowUnsafe: true,
    nullableContextOptions: NullableContextOptions.Disable,
    specificDiagnosticOptions: SuppressNonBlocking());
```

- One `CSharpCompilation` per assembly. Build an assembly dependency graph and compile in topological order; where an internal reference corresponds to an assembly built from source in the same run, use a `CompilationReference`, otherwise a `MetadataReference` to the DLL.
- **Never** inject fabricated types to silence missing references. Missing references are diagnostics; symbols that fail to bind are emitted with `isCompilerCertain: false`.
- Encoding: try UTF-8 with BOM, then UTF-8 strict, then CP932 (`System.Text.Encoding.CodePages`, register provider at startup). Record the chosen encoding per file.
- `SourceText` must be created with the detected encoding so that line/column positions match the compiler's view.

### 7.4 What the C# extractor emits

Per method body, using `SemanticModel` and `IOperation`:

| Emission | Source |
|---|---|
| `calls` edge | `IInvocationOperation`. `confidence = certain` for non-virtual/sealed/static targets; `candidate` for interface/virtual targets, one edge per implementation found in the compilation, sharing a `candidateGroup` |
| `creates` edge | `IObjectCreationOperation` |
| `reads` / `writes` edges | `IFieldReferenceOperation`, `IPropertyReferenceOperation`, assignment targets, `ref`/`out` arguments |
| `subscribes` / `unsubscribes` | `IEventAssignmentOperation`, and `X.clicked += handler` patterns |
| `typeOf` edge | `ITypeOfOperation` |
| `branchesTo` edges | From `ControlFlowGraph` basic blocks, emitted per method as a compact block list with guards |
| `conditions` on edges | Enclosing `if`/`switch`/`while` guard expressions, captured as text plus evidence; constant-folded where possible |
| Resolved arguments | Constant folding via `SemanticModel.GetConstantValue`, plus `Enumeration` static member recognition (§7.5) |
| Lambdas / local functions | Separate `method` nodes with `containedIn` edge to the enclosing method. **Not** inlined as synchronous calls |
| `async void`, `Task.Run`, UniTask switches | `deferred: true` on the resulting call edge |

Exclusions:

- Trivia (comments) is never emitted as a fact. `▼ADD/▼MOD/▼CHG/▼UPD/▼DEL` markers are captured only into a `changeMarkers` side table (file, line, marker, date, author, issue id) for reporting.
- Inactive `#if` regions produce no nodes or edges because they are not in the syntax tree under the profile's preprocessor symbols.

### 7.5 `Enumeration` constant resolution

`ScreenID`, `StageNo`, `PsCode`, `ResultStatusCode` are classes deriving from `Enumeration` with `public static readonly` members. The extractor must:

1. Identify types whose base type chain reaches `Enumeration` (`Assets/Common/Script/Base/Enumeration.cs`).
2. Emit a `field` node per static member, with the constructor's constant arguments captured in `attributes`-like form (`enumerationValue`: `{ id, name }`).
3. When such a member is used as an argument, emit `kind: "enumerationMember"` with `targetNodeId`.
4. Merge members across `partial` declarations.

---

## 8. Domain adapters (Harness.Domain.UnityPos)

Each adapter reads IR records plus, where stated, raw resource files, and emits additional domain edges and domain tables. Anchors (type names, method names, paths) are externalised in `config/domain.unitypos.json` so they can be tuned without recompiling.

### 8.1 Screen registry

- Input: `Scene_PosRegister_View.InitDictionary` and `Scene_Customer_View` equivalent — dictionary initialisers mapping a `ScreenID` member to `typeof(View)` / `typeof(Presenter)`.
- Emit: `screen` rows `(screenId, terminal, viewTypeId, presenterTypeId, sceneAssembly)` and `registersScreen` edges.
- `terminal` is derived from which registry contained the entry: `employee` or `customer`.
- Screens present in `ScreenID` but absent from every registry are recorded with `registered: false` — they are a known source of break links.

### 8.2 Navigation

- Input: calls to navigation methods (`DisplayTargetScreen`, `DisplayTargetPopup`, and the configured list) whose argument resolves to a `ScreenID` member.
- Emit: `navigatesTo` edge from the calling method node to the target `screen` node, carrying the enclosing guard conditions.
- Argument unresolved → `confidence: unresolved`, break category `RuntimeStateDependentDispatch`.

### 8.3 Key sequence tables

- Input: `iKeySeqReg.Add(...)` calls in `NmlKty*.cs`, with resolved `StageNo`, `PsCode`, handler delegate and declared next `StageNo`.
- Emit: `keyseq_transitions` rows `(stage, psCode, handlerMethodId, nextStage)` and `registersKeySeq` edges.
- Input: `KeySeqModel.ExeKeyProc(psCode)` call sites.
- Emit: `dispatchesKeySeq` edges to **all** transitions matching the resolved `psCode`, grouped by `candidateGroup`, each annotated with the required `stage`. If the current stage cannot be determined statically (the normal case), every such edge is `candidate` and contributes a break of category `KeySeqStageDependent`.
- The dispatcher semantics "set next stage first, restore on failure" must be recorded as a note on the transition rows.

### 8.4 Terminal-to-terminal TCP

- Senders: `SslSend(...)` (and configured siblings) with a resolved string first argument → `sendsSocket` edge to `externalBoundary` node `X:tcp:<eventPath>`.
- Receivers: methods overriding `OnReceiveSocket`; extract the string literals they compare against (`==`, `Equals`, `Contains`, `StartsWith`, `switch` labels, `Split` + compare).
- Pair sender and receiver by event path. Because dispatch targets "the screen currently displayed on the other terminal", **all** receivers matching the path are emitted as `receivesSocket` candidates in one `candidateGroup`.
- Emit break of category `TerminalTcpCurrentScreen` when the candidate count is greater than one.
- Senders whose argument does not resolve to a constant are `unresolved` and reported — they are not silently dropped.

### 8.5 Devices (hardware terminals)

- Outbound: invocations on members of the `I*Command` interfaces under `Assets/PosModule/DeviceBroker/` → `sendsDeviceCommand` edge to `X:device:<InterfaceName>.<Member>`.
- Inbound: implementations of `IDeviceBrokerMessageEvent` / `OnMessageReceived(...)` in device controllers; MQTT topic constants compared inside them → `receivesDeviceEvent` edges from `X:device:<topic>`.
- Emit break of category `ExternalHardware` for every outbound call, recording:
  - the interface and member invoked,
  - the request DTO type,
  - the expected response DTO type (method return type, e.g. `EvRwHttpResponse`),
  - the MQTT topic if determinable,
  - **resume candidates**: the controller callback(s) and the screen/presenter handlers they dispatch to.

### 8.6 Microservice API

- Input: `ApiRequest` creation, `SendMicroserviceRequest*` calls, `apiKey` constants from `Assets/Common/Script/API/Const/ApiConst.cs`.
- Emit: `callsApi` edge to `X:api:<apiKey>`, with request/response DTO types.
- Resume candidate: the continuation/callback or mapper that consumes the response.
- Break category `ExternalMicroservice`.
- Local mock files under `Assets/StreamingAssets/MSLocal/` are recorded as `mockAvailable: true` on the API row (useful downstream, not used to claim the boundary is resolved).

### 8.7 Native interop

- Input: `AndroidJavaObject` / `AndroidJavaClass` construction and calls, `[DllImport]`.
- Emit: `crossesBoundary` to `X:native:<class-or-lib>`, break category `NativeInterop`, no resume candidates unless a managed callback is registered in the same statement sequence.

### 8.8 Database mediation

- Input: string literals reaching SQLite execution APIs; parse with a lightweight SQL tokenizer to extract table names and statement type.
- Emit: `readsTable` / `writesTable` edges to `table` nodes.
- When a path requires a value produced by another feature through a table, emit break category `DatabaseMediated`, with the table as the contract and all writers of that table as resume candidates.

### 8.9 DI, queues, UI

| Adapter | Emission |
|---|---|
| `ServiceLocator` | `registersService` from registration sites (including factory registrations sharing one singleton); `resolvesService` from `GetService<T>()`. One registration → `certain`; several → `candidate` + break `DiAmbiguous`. Reflection-based screen construction → break `ReflectionConstruction` |
| Model queue | `ModelQueueing.AddQueue(lambda)` → `enqueues` edge to the lambda's method node with `deferred: true`; break category `DeferredExecution` recorded on the path, not treated as a dead end |
| UXML | Parse `*.uxml` with a hardened XML reader (`DtdProcessing.Prohibit`, `XmlResolver = null`); extract control name, type, static text, hierarchy path |
| Control binding | `Q<T>("name")` / `Query` calls in View types → `bindsControl` edge from the View to the UXML control; `clicked += handler` → `calls` edge control → handler |
| Screen initialize JSON | `Assets/Common/View/Resources/ScreenInitialize/<Screen>_Initialize.json` → control index → localization key |
| Localization | `Assets/Localization/StringTable/` → `(key, locale) → text`. Default locale `ja`; others available on request. Rich-text tags stripped into `textPlain`, original kept in `textRaw` |
| Input constraints | `SetInputMaxValue`, `SetInputMinValue`, `SetInputMaxNumberOfCharacter`, `SetInputAllowEmptyString` (configured list) → `constrainsInput` rows `(screenId, controlId, constraintKind, value, errorCode)`; error code resolved to text through the message table |

### 8.10 Domain configuration file

`config/domain.unitypos.json` holds every project-specific anchor: navigation method names, socket send/receive method names, device interface namespace, API constant type, queue type, input-constraint method names, registry type names, localization paths, standby screen IDs. No anchor may be hard-coded in C#.

---

## 9. Index storage

SQLite, one file per index (default `myTestV1/.index/harness.db`). WAL mode, synchronous=NORMAL during build, foreign keys on.

```sql
CREATE TABLE snapshots (
  snapshot_id   TEXT PRIMARY KEY,
  profile_id    TEXT NOT NULL,
  created_at    TEXT NOT NULL,
  project_root  TEXT NOT NULL,
  unity_declared TEXT,
  unity_cache    TEXT,
  derivation     TEXT NOT NULL,      -- 'native' | 'derived'
  ir_schema      TEXT NOT NULL,
  rules_version  TEXT NOT NULL,
  file_count     INTEGER NOT NULL,
  input_digest   TEXT NOT NULL
);

CREATE TABLE files (
  snapshot_id TEXT, path TEXT, assembly TEXT, encoding TEXT,
  content_hash TEXT, line_count INTEGER,
  PRIMARY KEY (snapshot_id, path));

CREATE TABLE nodes (
  snapshot_id TEXT, id TEXT, kind TEXT, name TEXT, display_name TEXT,
  namespace TEXT, declaring_type TEXT, assembly TEXT, file TEXT,
  start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER,
  modifiers TEXT, return_type TEXT, parameters_json TEXT,
  compiler_certain INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (snapshot_id, id));

CREATE TABLE edges (
  snapshot_id TEXT, id TEXT, kind TEXT, from_id TEXT, to_id TEXT,
  confidence TEXT NOT NULL, candidate_group TEXT, deferred INTEGER NOT NULL DEFAULT 0,
  conditions_json TEXT, arguments_json TEXT,
  PRIMARY KEY (snapshot_id, id));
CREATE INDEX ix_edges_from ON edges (snapshot_id, from_id, kind);
CREATE INDEX ix_edges_to   ON edges (snapshot_id, to_id, kind);

CREATE TABLE evidence (
  snapshot_id TEXT, id TEXT, file TEXT,
  start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER,
  snippet_hash TEXT,
  PRIMARY KEY (snapshot_id, id));

CREATE TABLE edge_evidence (
  snapshot_id TEXT, edge_id TEXT, evidence_id TEXT,
  PRIMARY KEY (snapshot_id, edge_id, evidence_id));

CREATE TABLE screens (
  snapshot_id TEXT, screen_id TEXT, terminal TEXT,
  view_type_id TEXT, presenter_type_id TEXT, registered INTEGER NOT NULL,
  display_name TEXT, business_id TEXT,
  PRIMARY KEY (snapshot_id, screen_id, terminal));

CREATE TABLE controls (
  snapshot_id TEXT, screen_id TEXT, terminal TEXT, control_id TEXT,
  control_type TEXT, uxml_path TEXT, hierarchy_path TEXT,
  localization_key TEXT, handler_method_id TEXT,
  PRIMARY KEY (snapshot_id, screen_id, terminal, control_id));

CREATE TABLE texts (
  snapshot_id TEXT, key TEXT, locale TEXT, text_plain TEXT, text_raw TEXT,
  PRIMARY KEY (snapshot_id, key, locale));

CREATE TABLE messages (
  snapshot_id TEXT, code TEXT, locale TEXT, text_plain TEXT,
  PRIMARY KEY (snapshot_id, code, locale));

CREATE TABLE input_constraints (
  snapshot_id TEXT, screen_id TEXT, terminal TEXT, control_id TEXT,
  constraint_kind TEXT, value TEXT, unit TEXT, error_code TEXT,
  evidence_id TEXT,
  PRIMARY KEY (snapshot_id, screen_id, terminal, control_id, constraint_kind));

CREATE TABLE keyseq_transitions (
  snapshot_id TEXT, stage TEXT, ps_code TEXT,
  handler_method_id TEXT, next_stage TEXT, evidence_id TEXT,
  PRIMARY KEY (snapshot_id, stage, ps_code, handler_method_id));

CREATE TABLE screen_edges (
  snapshot_id TEXT, id TEXT,
  from_screen TEXT, from_terminal TEXT,
  to_screen TEXT, to_terminal TEXT,
  trigger_kind TEXT,            -- click | input | scan | deviceEvent | socket | auto | keySeq
  control_id TEXT,
  confidence TEXT NOT NULL,
  candidate_group TEXT,
  chain_json TEXT NOT NULL,     -- ordered method chain, §11.2
  conditions_json TEXT,
  break_ids TEXT,               -- comma-separated break ids traversed by this edge
  PRIMARY KEY (snapshot_id, id));
CREATE INDEX ix_screen_edges_from ON screen_edges (snapshot_id, from_screen, from_terminal);
CREATE INDEX ix_screen_edges_to   ON screen_edges (snapshot_id, to_screen,   to_terminal);

CREATE TABLE breaks (
  snapshot_id TEXT, id TEXT, category TEXT NOT NULL, reason_code TEXT NOT NULL,
  at_node_id TEXT, at_evidence_id TEXT,
  contract_kind TEXT, contract_key TEXT,
  request_type TEXT, response_type TEXT,
  resume_candidates_json TEXT, resume_confidence TEXT,
  detail TEXT,
  PRIMARY KEY (snapshot_id, id));

CREATE TABLE external_boundaries (
  snapshot_id TEXT, id TEXT, category TEXT, key TEXT, detail_json TEXT,
  PRIMARY KEY (snapshot_id, id));

CREATE TABLE change_markers (
  snapshot_id TEXT, file TEXT, line INTEGER, marker TEXT,
  date TEXT, author TEXT, issue_ref TEXT);

CREATE TABLE diagnostics (
  snapshot_id TEXT, severity TEXT, code TEXT, message TEXT,
  file TEXT, line INTEGER);
```

---

## 10. Build pipeline

`harness index build --profile <id>` executes:

1. **Preflight** — §7.1 checks; abort on `E_NO_COMPILATION_INPUT`.
2. **Compile** — build all assemblies in dependency order.
3. **Extract** — run `IFactExtractor`, streaming IR records into `nodes`/`edges`/`evidence` in batched transactions (10k records per transaction).
4. **Resources** — parse UXML, ScreenInitialize JSON, localization and message tables.
5. **Domain adapters** — run §8 adapters in this fixed order: screens → controls/UI → DI → keyseq → navigation → TCP → device → API → SQL → queue.
6. **Screen graph** — build `screen_edges` (§11).
7. **Breaks** — detect and persist (§12).
8. **Validate & report** — emit counts per table, unresolved counts per category, and the acceptance checks of §18.
9. **Seal snapshot** — write `snapshots` row with `input_digest` = hash over (file content hashes + profile + rules version).

Determinism requirement: two builds over identical inputs must produce byte-identical `nodes`, `edges`, `screen_edges` and `breaks` content (ordering by id). Any use of unordered parallel iteration must sort before insert.

Progress is written to **stderr** as newline-delimited JSON so stdout stays a single clean JSON document.

---

## 11. Screen operation graph

### 11.1 Nodes and edges

- Node: `(screenId, terminal)`.
- Edge: one user operation or device event that causes the displayed screen to change on either terminal.

Edge construction: starting from each screen's View/Presenter types, follow `bindsControl` → handler → `calls`/`enqueues`/`dispatchesKeySeq`/`sendsSocket`/`sendsDeviceCommand`/`callsApi` transitively until a `navigatesTo` edge is reached, a boundary is crossed, or the depth limit is hit.

Traversal rules:

- Depth limit `maxChainDepth` (default 40) on the method chain; exceeding it produces reason `TruncatedByDepthLimit` and the edge is `unresolved`.
- `deferred` edges are followed, but the flag is propagated into the chain so the caller can see execution is queued.
- Cross-terminal: `sendsSocket` → matching `receivesSocket` candidates. The edge's `to_terminal` is the other terminal.
- Candidate fan-out is capped at `maxCandidatesPerDispatch` (default 12); exceeding it truncates and records `TruncatedByCandidateLimit`.

### 11.2 Chain representation

`chain_json` is an ordered array; each element:

```json
{
  "order": 3,
  "kind": "call|navigate|socketSend|socketReceive|deviceCommand|deviceEvent|apiCall|keySeqDispatch|enqueue|dbRead|dbWrite",
  "nodeId": "…",
  "namespace": "SejPos.Customer.EdyCharge",
  "type": "EdyCharge_SSAW0907_Presenter",
  "method": "OnClickBtnOk()",
  "file": "Assets/SceneCustomer/.../EdyCharge_SSAW0907_Presenter.cs",
  "span": { "startLine": 318, "startCol": 9, "endLine": 318, "endCol": 34 },
  "confidence": "certain",
  "deferred": false,
  "conditions": [ { "expression": "inputValue.Length == 0", "evidenceId": "e_…" } ],
  "breakId": null,
  "evidenceIds": ["e_…"]
}
```

---

## 12. Break links

### 12.1 Definition

A **break** is a point where the analysed code hands control or data to something the harness cannot follow with certainty. A break never silently terminates analysis: it is recorded, and a resume point is sought.

### 12.2 Categories (all eight are first-class in v1)

| Category | Trigger | Contract recorded | Resume candidates |
|---|---|---|---|
| `ExternalHardware` | Call on `DeviceBroker` `I*Command` member; MQTT publish | interface + member, request DTO, response DTO, topic | device controller `OnMessageReceived` and the handlers it dispatches to |
| `ExternalMicroservice` | `callsApi` | apiKey, request DTO, response DTO, HTTP method/path if known | response callback / mapper method |
| `TerminalTcpCurrentScreen` | `sendsSocket` with >1 matching receiver, or 0 receivers | event path string | all `OnReceiveSocket` overrides matching the path |
| `NativeInterop` | `AndroidJavaObject`/`AndroidJavaClass`/`DllImport` | java class or native lib + member | managed callback if registered, else none |
| `DatabaseMediated` | required value flows through a SQLite table | table name, columns if determinable | all writers of that table |
| `DiAmbiguous` | `GetService<T>()` with multiple registrations, or reflection-based construction | interface type, registered implementations | each implementation |
| `DeferredExecution` | `AddQueue`, `async void`, `Task.Run`, UniTask thread switch | queue type, lambda node | the lambda body (followed, but flagged) |
| `KeySeqStageDependent` | `ExeKeyProc` with runtime-determined stage | psCode, candidate stages | each matching transition handler |

Anything else that cannot be resolved is recorded with category `Unknown` and a reason code, never dropped.

### 12.3 Reason codes

`NoSourceForExternalSystem`, `RuntimeStateDependentDispatch`, `MultipleDiRegistrations`, `ReflectionConstruction`, `DeferredExecution`, `DataMediatedByDatabase`, `StringKeyedDispatchUnmatched`, `NativeInterop`, `UnresolvedSymbol`, `UnresolvedConstantArgument`, `ScreenNotRegistered`, `TruncatedByDepthLimit`, `TruncatedByCandidateLimit`.

### 12.4 Resume resolution

For a break with contract key `k` and category `c`:

1. Look up inbound handlers associated with `k` (device event handlers for a topic, response mappers for an apiKey, `OnReceiveSocket` for an event path, table writers for a table, implementations for an interface).
2. If exactly one handler is found → `resumeConfidence: "single"`; the screen edge continues through it and remains `candidate` overall (the external system's behaviour is still unproven).
3. If several → `resumeConfidence: "multiple"`; the path forks, each fork carrying the same `breakId`.
4. If none → `resumeConfidence: "none"`; the path terminates at the boundary and is returned as a **partial path** with `reachedTarget: false`.

### 12.5 Break record shape

```json
{
  "breakId": "b_4c21…",
  "category": "ExternalHardware",
  "reasonCode": "NoSourceForExternalSystem",
  "at": {
    "nodeId": "…",
    "namespace": "SejPos.DeviceBroker",
    "type": "EValueRwController",
    "method": "RequestCharge(EvRwChargeRequest)",
    "file": "Assets/PosModule/DeviceBroker/Impl/EValueRwController.cs",
    "span": { "startLine": 211, "startCol": 13, "endLine": 211, "endCol": 58 }
  },
  "contract": {
    "kind": "deviceInterface",
    "key": "IEValueRwCommand.RequestCharge",
    "topic": "iobox/evrw/command",
    "requestType": "EvRwChargeRequest",
    "responseType": "EvRwHttpResponse"
  },
  "why": "Control leaves the analysed assembly set: the IOBOX/IC-RW firmware is a separate system whose source is not present in this repository. The response arrives asynchronously over MQTT, so no static call edge exists between request and response.",
  "resume": {
    "confidence": "multiple",
    "candidates": [
      { "nodeId": "…", "type": "EValueRwController", "method": "OnMessageReceived(MqttApplicationMessageReceivedEventArgs)", "file": "…", "span": {} }
    ]
  }
}
```

`why` is generated from a fixed template per `(category, reasonCode)` plus the resolved contract values. It is never free-form model text.

---

## 13. Path search

### 13.1 Inputs

```text
from       : screen id (+ optional terminal)
to         : screen id (+ optional terminal)
k          : max paths, default 5
maxHops    : max screen hops, default 25
includePrefix : bool, default false
allowBreaks   : bool, default true
profile    : profile id
```

### 13.2 Algorithm

1. Resolve `from`/`to` to screen nodes. If a screen ID exists on both terminals and the terminal is unspecified, run the search for each and merge, tagging results.
2. Build an in-memory directed multigraph from `screen_edges` for the snapshot.
3. Edge cost:

   ```text
   cost = 1.0
        + (confidence == "candidate"   ? 0.5 : 0)
        + (confidence == "unresolved"  ? 2.0 : 0)
        + (deferred                    ? 0.2 : 0)
        + 0.75 * numberOfBreaksOnEdge
   ```

   Costs are deterministic; ties broken by `screen_edges.id` ascending.
4. Run **Yen's K-shortest loopless paths** over this cost. Loopless at screen-node level; a screen may still appear twice if it is on different terminals.
5. If fewer than `k` complete paths exist, append **partial paths**: best-first paths that terminate at a break with `resumeConfidence: "none"`, marked `reachedTarget: false`. These are the answer to "where does it break".
6. If `includePrefix` is true, run the same search from the configured standby screen set (`config/domain.unitypos.json: standbyScreens`, default `["SSAW0001"]` on the employee terminal) to `from`, take the single best path, and return it as `prefixPath`.

### 13.3 Output

```json
{
  "ok": true,
  "data": {
    "from": { "screenId": "SSAW0903", "terminal": "customer", "displayName": "…" },
    "to":   { "screenId": "SSAW0907", "terminal": "customer", "displayName": "…" },
    "prefixPath": { "…": "same shape as a path, or null" },
    "paths": [
      {
        "pathId": "p_1",
        "rank": 1,
        "cost": 4.25,
        "reachedTarget": true,
        "overallConfidence": "candidate",
        "hops": [
          {
            "order": 1,
            "from": { "screenId": "SSAW0903", "terminal": "customer" },
            "to":   { "screenId": "SSAW0907", "terminal": "customer" },
            "trigger": {
              "kind": "click",
              "controlId": "BtnOtherAmount",
              "textJa": "その他の金額を入力（1,000円単位）",
              "localizationKey": "SSAW0903_12",
              "uxmlPath": "Assets/SceneCustomer/.../EdyCharge_SSAW0903_View.uxml"
            },
            "inputConstraints": [
              { "controlId": "InputAmount", "kind": "maxValue", "value": "25",
                "unit": "1000JPY", "errorCode": "ERRB022", "errorTextJa": "…" }
            ],
            "chain": [ { "…": "§11.2 chain elements" } ],
            "breaks": ["b_4c21…"],
            "confidence": "candidate"
          }
        ],
        "breaks": [ { "…": "§12.5 break records" } ],
        "unresolved": []
      }
    ],
    "truncated": false
  },
  "meta": {
    "snapshotId": "…", "profileId": "android-onsite", "derivation": "native",
    "k": 5, "maxHops": 25, "elapsedMs": 812, "rulesVersion": "1.0"
  }
}
```

`overallConfidence` is the weakest confidence among hops: `certain` only when every hop is `certain` and no break is traversed.

---

## 14. CLI contract

Executable: `harness` (`dotnet Harness.Cli.dll`).
Every command prints **exactly one JSON document to stdout**; progress and logs go to stderr.

| Command | Purpose |
|---|---|
| `index build --profile <id> [--config <path>] [--project <root>] [--db <path>]` | Full index build |
| `index list` | Snapshots in the database |
| `index verify --snapshot <id>` | Re-run validation and acceptance checks |
| `screen get --id <SCREENID> [--terminal <t>]` | Screen, types, registration, business id |
| `screen controls --id <SCREENID> [--terminal <t>] [--locale ja]` | Controls, display text, handlers |
| `screen constraints --id <SCREENID> [--terminal <t>]` | Input constraints + error codes/text |
| `screen transitions --id <SCREENID> [--direction out\|in]` | Outgoing/incoming screen edges |
| `path find --from A --to Z [--k 5] [--max-hops 25] [--include-prefix] [--terminal-from t] [--terminal-to t]` | Main query (§13) |
| `path prefix --to A [--k 1]` | Prefix path only |
| `break list [--path <pathId>] [--category <c>] [--screen <id>]` | Break records |
| `symbol find --name <n> [--kind <k>]` | Symbol lookup, candidates on ambiguity |
| `symbol callers --id <nodeId>` / `symbol callees --id <nodeId>` | Call relations with confidence |
| `symbol facts --id <nodeId>` | Calls, reads/writes, branches, returns, throws |
| `evidence get --id <evidenceId> [--context 3]` | Source snippet on demand |
| `text get --key <k> [--locale ja]` / `message get --code <c>` | Localized text |

Common options: `--profile`, `--db`, `--snapshot`, `--pretty`.

### 14.1 Envelope and errors

```json
{ "ok": false,
  "error": { "code": "E_AMBIGUOUS_SCREEN", "message": "...", "details": {} },
  "meta": { "snapshotId": "…" } }
```

Error codes: `E_NO_COMPILATION_INPUT`, `E_PROFILE_NOT_FOUND`, `E_SNAPSHOT_NOT_FOUND`, `E_STALE_SNAPSHOT`, `E_INVALID_INPUT`, `E_NOT_FOUND`, `E_AMBIGUOUS_SCREEN`, `E_LIMIT_EXCEEDED`, `E_IR_SCHEMA_MISMATCH`, `E_INTERNAL`.

Exit codes: `0` ok, `2` invalid input, `3` not found, `4` build failure, `5` internal error.

---

## 15. Python calling layer

No analysis logic. Responsibilities: locate the executable, run it, parse the envelope, raise typed exceptions, expose function-calling schemas.

```python
from harness_client import HarnessClient

client = HarnessClient(db="…/harness.db", profile="android-onsite")
result = client.path_find(from_screen="SSAW0903", to_screen="SSAW0907",
                          k=5, include_prefix=True)
for p in result.paths:
    for hop in p.hops:
        ...
    for b in p.breaks:
        print(b.category, b.reason_code, b.at.file, b.at.span.start_line, b.why)
```

- `client.py`: `subprocess.run` with `capture_output=True`, `text=True`, `encoding="utf-8"`; a configurable timeout; non-zero exit → `HarnessError(code, message, details)`.
- `models.py`: dataclasses mirroring §13.3 and §12.5, built with `dacite`-style strict parsing so schema drift fails loudly.
- `tools.py`: `TOOL_SCHEMAS` — a list of JSON-Schema function definitions (`harness_path_find`, `harness_screen_controls`, `harness_break_list`, `harness_evidence_get`, ...) ready to register with an LLM agent, plus `dispatch(name, arguments) -> dict`.

---

## 16. Confidence and anti-fabrication rules

These rules are implementation requirements, enforced in code and covered by tests.

1. Every returned fact carries `confidence` ∈ {`certain`, `candidate`, `unresolved`} and at least one evidence id.
2. `certain` is permitted only when the compiler semantics determine the target uniquely (static/sealed/non-virtual call, resolved constant, single registration).
3. Candidate sets are returned in full, never reduced to a single "most likely" element by heuristics.
4. Truncation is always reported (`truncated: true` plus the limit that fired). A truncated result must never be presented as exhaustive.
5. Comments and commented-out code are never a fact source.
6. A screen, control, text, constraint or error code that is not present in the index is reported as absent, never synthesised.
7. Symbols are never matched by simple name; all resolution goes through node ids.
8. If the snapshot's `input_digest` no longer matches the current working tree, queries still answer but set `meta.stale = true`.

---

## 17. Testing

### 17.1 Unit tests (fixtures, not the real repo)

Small synthetic projects under `tests/fixtures/` covering: rsp parsing (quotes, nested `@`, relative paths), CP932 decoding, `#if` exclusion, partial type merge, duplicate simple names, `Enumeration` member resolution, `goto`-heavy CFG, candidate fan-out, each of the eight break categories, Yen's K-paths determinism.

### 17.2 Golden tests

For a fixture project, the full `index build` output is compared against committed golden JSON to guarantee determinism.

### 17.3 Architecture test

A test asserts that `Harness.Core`'s compiled assembly references neither `Microsoft.CodeAnalysis.*` nor any Unity assembly. This protects the cross-language reuse requirement.

---

## 18. Acceptance criteria (against the real repository)

The verified Edy charge flow is the v1 acceptance baseline. With `--profile android-onsite`:

1. `screen get --id SSAW0903` returns terminal `customer`, view `EdyCharge_SSAW0903_View`, presenter `EdyCharge_SSAW0903_Presenter`. Same for `SSAW0907`.
2. `screen controls --id SSAW0903 --locale ja` includes a control whose text is `その他の金額を入力（1,000円単位）`, resolved through `EdyCharge_SSAW0903_Initialize.json` key `SSAW0903_12` and the Customer string table.
3. `screen constraints --id SSAW0907` returns: max digits `2` → `ERRB002`; max value `25` → `ERRB022`; min value `1` → `ERRB022`; empty input not allowed → `ERRB024`. The source comment states the opposite mapping for `ERRB002`/`ERRB022`; the harness must follow the code, and a test asserts this.
4. `path find --from SSAW0903 --to SSAW0907` returns at least one path whose hops traverse: customer `SSAW0903` → socket send `PosRegister.SSAW0281.Other` → employee receiver **candidates** (at least `Payment_SSAW0089_Presenter` and `EdyCharge_SSAW0281_Presenter`) → socket send back (`SSAW0903.To.SSAW0907` / `ToSSAW0907`) → customer `SSAW0907`.
5. That path reports a `TerminalTcpCurrentScreen` break with all receiver candidates listed and `why` explaining that the receiver is the screen currently displayed on the other terminal.
6. `path prefix --to SSAW0903` produces a path reaching `SSAW0903` and reports the inbound event `Customer.NextDispReq.SSAW0903` with its sender candidates (expected to include `SPOW0309_Presenter`, `Payment_SSAW0089_Presenter`, `Payment_SSAW0365_Presenter`).
7. No hop on these results is marked `certain` where the target depends on the current screen or stage.
8. `break list --category ExternalHardware` returns entries for the IC-RW/Edy device commands with `requestType`, `responseType`, topic and the `OnMessageReceived` resume candidate.
9. `index build` is deterministic: two consecutive builds produce identical content hashes for `nodes`, `edges`, `screen_edges`, `breaks`.

A result that omits a known candidate, or that promotes a candidate to `certain`, is a failure even if it "looks" right.

---

## 19. Operational notes

- Prerequisite: the user opens the project in Unity 2022.3.68f1 and compiles for the target platform so `Library/Bee/artifacts/*.dag/*.rsp` exists. Android profile accuracy depends on the cache having been produced for Android; otherwise the snapshot is marked `derived`.
- Prerequisite: .NET 8 SDK (present on the build machine).
- Expected magnitude: ~9,400 files / ~2.4M lines. Build is a batch job; it must stream, batch transactions, and report progress. Memory is bounded by processing one assembly at a time and releasing each `SemanticModel` after use.
- Index database and any extracted design-document derivatives stay local. Confidential design material is never embedded in query output beyond the minimum needed (file path + span + on-demand snippet).
- Secrets, tokens and absolute machine paths must not appear in output; paths are repository-relative.

---

## 20. Deferred to later phases

| Phase | Item |
|---|---|
| v1.1 | Incremental index update with dependency tracking and invalidation |
| v1.2 | MCP stdio server wrapping the same query services |
| v1.3 | CI bypass-variant profiles (`ci/Bypass*.py`, `BypassNewIOBox.patch` applied before analysis) |
| v2 | Runtime trace build, trace ingestion, candidate → observed reconciliation |
| v2 | Additional language extractors (Java, Python) against the same IR |
| v2 | Test-data catalogue and expected-value computation |
