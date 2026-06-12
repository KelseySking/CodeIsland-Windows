# CodeIsland for Windows

<p align="center">
  <img src="imgs/logo.png" alt="CodeIsland app icon" width="128" />
</p>

> see what your AI coding agent is doing in real time without switching windows.

[中文版本](README_CN.md)

CodeIsland is an **AI coding agent status panel**. This project is a Windows adaptation inspired by the popular macOS open-source project, [CodeIsland](https://github.com/wxtsky/CodeIsland). The macOS version anchors to the MacBook notch area, while the Windows version is presented as a desktop HUD floating window. The current Windows version supports Claude Code and Codex. It listens to real-time events through the Hook mechanism and shows session status, permission approvals, Q&A interactions, and recent task details at the top of the screen.

Project repository: https://github.com/KelseySking/CodeIsland-Windows

## Screenshots

<p align="center">
  <img src="imgs/hud.png" alt="Collapsed HUD screenshot" width="640" />
</p>

<p align="center">
  <img src="imgs/hud-expanded.png" alt="Expanded HUD screenshot" width="760" />
</p>

## Features

- **Real-time agent monitoring** — Claude Code and Codex are supported end-to-end; Gemini CLI, Cursor, GitHub Copilot, Cline, and other tools are planned for future integration
- **Desktop HUD floating window** — Floating display at the top, side, or bottom of the screen, with automatic collapsed/expanded switching and no focus stealing
- **Session list and details** — View running status, current tool, recent messages, completion summaries, and task details in real time
- **Permission approvals** — Approve or deny tool permission requests directly from the panel, with global hotkey support
- **Q&A interaction** — Answer questions from AI tools directly in the panel without switching back to the terminal window
- **Terminal jump** — Jump to the corresponding terminal tab with one click, including precise Windows Terminal tab-level switching
- **Automatic Hook installation** — Install or uninstall the CodeIsland Hook for Claude Code and Codex from the settings UI
- **Webhook forwarding** — Asynchronously deliver key notifications to a custom HTTP(S) endpoint
- **8-bit sound effects** — Pixel-style sound effects for session start, completion, approval, and other events
- **Global hotkeys** — Default hotkeys: `Ctrl+Alt+I` to toggle the panel, `Ctrl+Alt+Y` to approve, and `Ctrl+Alt+N` to deny
- **Automatic updates** — Check for new versions through this project's GitHub Releases

## Tech Stack

| Component | Choice |
|------|------|
| Language/runtime | C# / .NET 8 (`net8.0` / `net8.0-windows`) |
| UI framework | WPF + WPF-UI |
| IPC | Named Pipes, 4-byte little-endian length prefix + UTF-8 JSON |
| Local API | ASP.NET Core Minimal API + WebSocket, bound to `127.0.0.1` by default |
| Process query | WMI / `System.Management` |
| Sound effects | NAudio |
| Hotkeys | `RegisterHotKey` P/Invoke |
| Testing | xUnit |

## Project Structure

```text
CodeIsland.Windows/
├── src/
│   ├── CodeIsland.Contracts/     # Local API / Hub DTO contracts
│   ├── CodeIsland.Core/          # Platform-independent core library
│   │   ├── Models/               # HookEvent, SessionSnapshot, AgentStatus, SupportedSource
│   │   ├── Services/             # EventNormalizer, ConfigInstaller, L10n, SettingsManager
│   │   └── IPC/                  # Named Pipe paths and message protocol
│   ├── CodeIsland.Bridge/        # Short-lived Hook Bridge: stdin JSON → Named Pipe
│   │   ├── Program.cs
│   │   ├── ProcessAncestry.cs    # WMI process ancestry parsing
│   │   ├── SourceResolver.cs     # AI tool source identification
│   │   └── EnvironmentCollector.cs
│   ├── CodeIsland.Hub/           # Local Hub: CLI operations, source management, HTTP/WebSocket API
│   └── CodeIsland.WpfApp/        # WPF main application
│       ├── ViewModels/           # WpfAppState and HUD view models
│       ├── Views/                # HUD, session list, approvals, Q&A, details, settings, about
│       ├── Services/             # HookServer, HubStateAdapter, TerminalActivator, GlobalHotkey, UpdateChecker
│       └── Assets/               # Icons and sound effects
├── tests/
│   ├── CodeIsland.Core.Tests/
│   ├── CodeIsland.Bridge.Tests/
│   └── CodeIsland.Hub.Tests/
├── scripts/                      # Build, publish, and packaging scripts
└── docs/                         # Technical specs, changelog, hardware/rendering notes
```

The dependency direction is layered around Bridge/Hub/Core: `CodeIsland.Bridge → CodeIsland.Core`, `CodeIsland.Hub → CodeIsland.Contracts + CodeIsland.Core`, and `CodeIsland.WpfApp → CodeIsland.Hub + CodeIsland.Core`. Bridge remains the short-lived Hook adapter; WPF displays and operates state through Hub-facing interfaces/API.

## Quick Start

### Requirements

- Windows 10/11
- .NET 8 SDK

### Build

This repository uses `CodeIsland.slnx`. Running `dotnet build` at the repository root builds all projects in the solution.

```powershell
dotnet build
dotnet build -c Release
```

You can also run the full build script:

```powershell
.\scripts\build.ps1
```

### Start the app in development

Start the WPF main application from the repository root:

```powershell
dotnet run --project src/CodeIsland.WpfApp

```

After startup, the HUD floating window is shown and a CodeIsland icon is created in the system tray. Hook events are forwarded to the main application by `CodeIsland.Bridge` as a short-lived child process. You do not need to keep Bridge running manually.
The main application also starts a local API at `http://127.0.0.1:32145` by default. The API token is stored as `api_token` in `%APPDATA%\CodeIsland\settings.json`.

### Run tests

```powershell
# Run all tests
dotnet test

# Run a single test project
dotnet test tests/CodeIsland.Core.Tests
dotnet test tests/CodeIsland.Bridge.Tests

# Run tests matching a name
dotnet test tests/CodeIsland.Core.Tests --filter FullyQualifiedName~EventNormalizer
```

### Publish single-file build

```powershell
.\scripts\publish-single-file.ps1
```

### Package release ZIP

```powershell
.\scripts\create-release-zip.ps1
```

### Package Windows installer

Install Inno Setup 6 first and ensure `ISCC.exe` is available in PATH, or specify its path with `-InnoSetupCompiler`.

```powershell
.\scripts\create-installer.ps1
```

### Start the app from a release package

After extracting the release ZIP, run:

```powershell
.\CodeIsland-Windows.exe
```

After the first startup, you can install the Hook for Claude Code and Codex from the Hooks tab in the settings UI. Other AI tool integrations are not yet open.

## How It Works

```text
AI tool triggers a Hook event (currently Claude Code and Codex)
  → codeisland-bridge.exe (started once per Hook invocation)
    → Read JSON from stdin
    → Collect Windows environment variables
    → Query process ancestry through WMI to identify the source CLI and tracked process
    → Send enriched JSON to the main application through Named Pipe
      → HookServer receives and routes by event type
        → Permission request / Q&A request → AppState waits for user action and returns the Hook response
        → Lifecycle event → SessionSnapshot.ReduceEvent() computes the new state
          → AppState updates global state, PanelWindow renders the HUD
```

`PermissionRequest`, `PreToolUse` events that explicitly require approval, and `Notification`/`Question*` events with question payloads are treated as blocking events and wait for user responses. Normal events first return a `{}` ack and then update the UI asynchronously, avoiding event loss or pipe noise caused by the short-lived Bridge disconnecting too early.

## Local Hub API

The WPF HUD is the default client. Web, plugin, and other local frontends can use the same localhost API. The first phase is local-only; LAN/mobile/watch pairing is not implemented yet.

For external display development, see `docs/external-display-client.md`. A no-dependency console sample is available at `samples/external-display-console`.

Authentication supports:

- `Authorization: Bearer <api_token>`
- `X-CodeIsland-Token: <api_token>`
- WebSocket clients can use `ws://127.0.0.1:32145/api/events?token=<api_token>`

Current API surface:

- `GET /api/health`
- `GET /api/version`
- `GET /api/capabilities`
- `GET /api/sources`
- `POST /api/sources/{source}/install`
- `POST /api/sources/{source}/uninstall`
- `POST /api/sources/{source}/repair`
- `GET /api/runtime-assets`
- `POST /api/runtime-assets/repair`
- `GET /api/sessions`
- `GET /api/sessions/{sessionId}`
- `GET /api/sessions/{sessionId}/messages`
- `GET /api/pending`
- `POST /api/permissions/{actionId}/allow`
- `POST /api/permissions/{actionId}/deny`
- `POST /api/questions/{actionId}/answer`
- `POST /api/questions/{actionId}/answer-current`
- `POST /api/questions/{actionId}/dismiss`
- `WS /api/events`

## Hook Installation

The user-visible Hook installation entry currently supports Claude Code and Codex:

| Format | Tool | Verified Version | Status |
|------|------|------|------|
| `.claude` | Claude Code | `v2.1.145` | Supported |
| `.codex` | Codex | `v0.137.0` | Supported |

Gemini CLI, Cursor, GitHub Copilot, Cline, and other tools are planned for future integration. Reserved underlying formats do not mean they are available in the current version.

You can install or uninstall the Claude Code and Codex Hooks with one click from the Hooks tab in the settings UI. The installer only adds or removes CodeIsland-owned hook entries and preserves existing user hooks, `env`, `permissions`, and other configuration.

## Configuration

The settings file is located at `%APPDATA%\CodeIsland\settings.json` and can be edited through the settings UI or directly.

| Category | Setting | Default |
|------|--------|--------|
| General | Start on boot | `false` |
| General | Display position | `top-center` |
| Behavior | Automatically approve safe tools | `true` |
| Behavior | Webhook URL | Empty string |
| Behavior | Session timeout (seconds) | `300` |
| Behavior | Smart suppression | `true` |
| Behavior | Hide in fullscreen | `true` |
| Appearance | Panel height mode | `auto` |
| Appearance | Show full recent messages | `false` |
| Sound | Enable sound effects | `true` |
| Sound | Volume | `0.7` |
| Hotkeys | Toggle panel | `Ctrl+Alt+I` |
| Hotkeys | Approve | `Ctrl+Alt+Y` |
| Hotkeys | Deny | `Ctrl+Alt+N` |

## Development Notes

- After modifying UI/WPF-related code, run at least `dotnet build` to verify XAML, bindings, and project references.
- When changes involve HUD behavior, approval/Q&A interaction, or window sizing and positioning, manually verify with `dotnet run --project src/CodeIsland.WpfApp`.
- Hook commands should call `codeisland-bridge.exe` directly. Do not wrap stdin forwarding with PowerShell `$input | & ...`.
- Bridge is published as trimmed/single-file, so enriched payload serialization must remain trim-safe.

## Distribution

| Channel | Priority |
|------|--------|
| GitHub Releases ZIP | P0 (MVP) |
| WinGet | P1 |
| Scoop | P2 |

## Acknowledgements

This project was heavily inspired by the excellent open-source project on macOS. Special thanks to:

- **[CodeIsland (macOS)](https://github.com/wxtsky/CodeIsland)** — Gratitude to the original author [@wxtsky](https://github.com/wxtsky) for the brilliant concept and inspiration. This Windows port extends the core philosophy of "monitoring AI agents in real-time without switching windows," specifically tailored for the Windows desktop environment (HUD overlay, Windows Terminal integration, etc.).

## License

MIT License

<center>This project has been shared on the [LINUX DO](https://linux.do).</center>
