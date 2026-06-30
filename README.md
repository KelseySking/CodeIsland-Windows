# CodeIsland for Windows

<p align="center">
  <img src="imgs/logo.png" alt="CodeIsland app icon" width="128" />
</p>

> see what your AI coding agent is doing in real time without switching windows.

[中文版本](README_CN.md)

CodeIsland is an **AI coding agent status panel** for Windows. Inspired by the macOS open-source project [CodeIsland](https://github.com/wxtsky/CodeIsland), the Windows version is presented as a desktop HUD floating window. It supports multiple AI coding tools through the bundled CodeOrbit Runtime, listens to real-time events through the Hook mechanism, and shows session status, permission approvals, Q&A interactions, and recent task details at the top of the screen.

Project repository: https://github.com/KelseySking/CodeIsland-Windows

## Screenshots

<p align="center">
  <img src="imgs/hud.png" alt="Collapsed HUD screenshot" width="640" />
</p>

<p align="center">
  <img src="imgs/hud-expanded.png" alt="Expanded HUD screenshot" width="760" />
</p>

<p align="center">
  <img src="imgs/Approval.png" alt="Expanded HUD screenshot" width="760" />
</p>

## Features

- **Real-time agent monitoring** — Supports multiple AI coding tools through CodeOrbit Runtime source plugins
- **Desktop HUD floating window** — Floating display at the top, side, or bottom of the screen, with automatic collapsed/expanded switching and no focus stealing
- **Session list and details** — View running status, current tool, recent messages, completion summaries, and task details in real time
- **Permission approvals** — Approve or deny tool permission requests directly from the panel, with global hotkey support
- **Q&A interaction** — Answer questions from AI tools directly in the panel without switching back to the terminal window
- **Terminal jump** — Jump to the corresponding terminal tab with one click, including precise Windows Terminal tab-level switching
- **Automatic Hook installation** — Connect or disconnect supported tools from the settings UI
- **Webhook forwarding** — Asynchronously deliver key notifications to a custom HTTP(S) endpoint
- **8-bit sound effects** — Pixel-style sound effects for session start, completion, approval, and other events
- **Global hotkeys** — Default hotkeys: `Ctrl+Alt+I` to toggle the panel, `Ctrl+Alt+Y` to approve, and `Ctrl+Alt+N` to deny
- **Automatic updates** — Check for new versions through GitHub Releases

## Tech Stack

| Component | Choice |
|------|------|
| Language/runtime | C# / .NET 8 |
| UI framework | WPF + WPF-UI |
| Sound effects | NAudio |
| Hotkeys | `RegisterHotKey` P/Invoke |

## Architecture

CodeIsland for Windows is a pure **display client** (HUD). It connects to a bundled [CodeOrbit Runtime](https://github.com/wxtsky/CodeOrbit) via REST and WebSocket APIs. The Runtime handles Hook event reception, state aggregation, and session management; the WPF app only handles UI rendering and user interaction.

```text
┌──────────────────────┐         ┌─────────────────────────┐
│ CodeOrbit Runtime    │◄────────│ CodeIsland-Windows      │
│ (bundled, auto-start)│  REST   │ (Pure Display Client)   │
│                      │  +WS    │                         │
│ Hook events → State  │         │ UI: HUD, approvals,     │
│ aggregation → API    │         │ Q&A, session details    │
└──────────────────────┘         └─────────────────────────┘
```

### Project Structure

```text
src/
├── CodeIsland.Contracts/     # API DTO contracts (aligned with CodeOrbit Runtime)
└── CodeIsland.WpfApp/        # WPF display client
    ├── ViewModels/           # App state and HUD view models
    ├── Views/                # HUD, session list, approvals, Q&A, details, settings
    ├── Services/             # Runtime API client, process manager, terminal, hotkeys, updates
    └── Assets/               # Icons and sound effects
scripts/                      # Build, publish, and packaging scripts
samples/
└── external-display-console/ # Example: connecting to Runtime API
```

## Quick Start

### Requirements

- Windows 10/11
- .NET 8 SDK

### Build

```powershell
dotnet build
dotnet build -c Release
```

### Run

```powershell
dotnet run --project src/CodeIsland.WpfApp
```

After startup, the HUD floating window is shown and a CodeIsland icon appears in the system tray. In managed mode, the app automatically starts the bundled `codeorbit-host.exe` and connects to `http://127.0.0.1:32145` via REST/WebSocket.

### Publish & Package

```powershell
# Single-file self-contained build
.\scripts\publish-single-file.ps1

# Release ZIP
.\scripts\create-release-zip.ps1

# Windows installer (requires Inno Setup 6)
.\scripts\create-installer.ps1
```

## Hook Installation

Open **Settings > Tool Connections** to connect or disconnect supported tools with one click. The list is provided by the bundled CodeOrbit Runtime plugins, so new Runtime releases can add or update tool integrations without changing the display client.

| Tool | Status |
|------|------|
| AntiGravity | Supported |
| Claude Code | Supported |
| Cline | Supported |
| CodeBuddy | Supported |
| Codex CLI | Supported |
| Cursor | Supported |
| Factory | Supported |
| Gemini CLI | Supported |
| GitHub Copilot | Supported |
| Hermes | Supported |
| Kimi Code | Supported |
| Kiro | Supported |
| OpenCode | Supported |
| Pi | Supported |
| Qoder | Supported |
| Qwen Code | Supported |
| StepFun | Supported |
| Trae | Supported |
| WorkBuddy | Supported |

## Configuration

Settings are stored at `%APPDATA%\CodeIsland\settings.json` and can be edited through the settings UI.

| Category | Setting | Default |
|------|--------|--------|
| General | Start on boot | `false` |
| General | Display position | `top-center` |
| Behavior | Auto-approve safe tools | `true` |
| Behavior | Webhook URL | Empty |
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

## Acknowledgements

This project was inspired by [CodeIsland (macOS)](https://github.com/wxtsky/CodeIsland) by [@wxtsky](https://github.com/wxtsky). The Windows version extends the core philosophy of "monitoring AI agents in real-time without switching windows" for the Windows desktop environment.

## License

MIT License

<center>This project has been shared on the [LINUX DO](https://linux.do).</center>
