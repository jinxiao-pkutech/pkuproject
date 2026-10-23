# ScreenFlow 部署说明

ScreenFlow 是 Unity C# POS 项目的静态解析工具：输入开始/结束画面 ID，输出画面间候选路径、每一步的源码处理链、API/硬件交互，以及全部断链。本目录是 Copilot 自定义 Agent 与 MCP 服务的部署文件。

## 目录结构

```
deploy/
├── .github/agents/screen-flow.agent.md   # Copilot 自定义 Agent
├── .github/skills/screen-flow/SKILL.md   # MCP 工具使用说明
├── .vscode/mcp.json                      # MCP 服务配置
└── README.md
```

## 部署步骤

1. 发布 CLI：

   ```bash
   dotnet publish src/ScreenFlow.Cli -c Release -r win-x64 --self-contained false -o publish
   ```

2. 安装 Python 依赖：

   ```bash
   pip install -e python
   ```

3. 构建索引（在终端执行，不暴露给 Agent）：

   ```bash
   dotnet run --project src/ScreenFlow.Cli -c Release -- index build \
     --project <Unity工程根> --profile android-onsite \
     --db <工程根>/.screenflow/android-onsite.db
   ```

   索引构建命令示例（Windows PowerShell）：

   ```powershell
   dotnet run --project src/ScreenFlow.Cli -c Release -- index build --project C:\pos\sej_client_pos_unity --profile android-onsite --db C:\pos\sej_client_pos_unity\.screenflow\android-onsite.db
   ```

4. 复制三个部署文件到 Copilot 能读到的位置（`.github/agents/`、`.github/skills/`、`.vscode/mcp.json`），并按实际路径调整 `mcp.json` 里的 `SCREENFLOW_CLI`、`SCREENFLOW_DB_DIR`、`SCREENFLOW_PROJECT`、`PYTHONPATH`。

5. 在 VS Code GitHub Copilot 中选择 `screen-flow` Agent，输入例如 `SSAW0903 SSAW0907` 验证。

## 常见问题

- **索引不存在（E_INDEX_NOT_FOUND）**：先在终端执行第 3 步的 `index build`。Agent 不暴露构建命令。
- **derivation = derived**：当前索引来自导出/重建而非原生构建，报告开头会醒目提示，并可能缺少部分字段。
- **stale = true**：源码或配置在索引构建后被改动，报告内容可能过期，需要重新 `index build`。
- **MCP 未启动**：确认 `mcp.json` 的 `command`/`args`/`env` 路径正确，且 `pip install -e python` 已执行；可先运行 `python -m screenflow_mcp --selftest` 自检。
- **CLI 返回 E_CLI_FAILURE**：检查 `SCREENFLOW_CLI` 指向的可执行文件或 `.dll` 是否可运行、`SCREENFLOW_TIMEOUT_SEC` 是否过小。
