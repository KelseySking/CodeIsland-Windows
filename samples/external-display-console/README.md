# 外部展示控制台示例

这是一个最小 CodeIsland Runtime API 展示客户端。它只使用 .NET 内置 HTTP 和 WebSocket API，因此不需要安装包，也不依赖 WPF 或 Hub 内部实现类型。

## 连接主应用

先启动 CodeIsland，然后运行：

```powershell
dotnet run --project samples/external-display-console
```

示例会尝试从 `%APPDATA%\CodeIsland\settings.json` 读取本地 token。

也可以显式传入 token：

```powershell
$settings = Get-Content "$env:APPDATA\CodeIsland\settings.json" | ConvertFrom-Json
dotnet run --project samples/external-display-console -- --token $settings.api_token
```

## 连接独立 RuntimeHost

```powershell
dotnet run --project src/CodeIsland.RuntimeHost -- --token dev-token --port 32145
dotnet run --project samples/external-display-console -- --token dev-token
```

## 命令

```text
refresh
allow <actionId> [always]
deny <actionId> [reason]
answer <actionId> <answer>[,<answer>...]
dismiss <actionId>
quit
```

`answer` 会调用 `POST /api/questions/{actionId}/answer-current`，这是逐步问答 UI 推荐使用的展示端操作。

完整开发者快速开始见 `docs/external-display-client.md`，稳定 Runtime/display contract 见 `docs/runtime-display-contract.md`。
