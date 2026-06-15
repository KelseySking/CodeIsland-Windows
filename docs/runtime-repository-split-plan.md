# CodeIsland Runtime Repository Split Plan

This document defines the first repository split for the Runtime/Hub base.
It is a migration plan, not a large source move. The goal is to make the
Runtime independently maintainable while keeping the Windows HUD usable during
each step.

## Decision Summary

ADR-lite decision: create a new `CodeIsland.Runtime` repository for the
Runtime base, and keep this repository as the Windows display client repository
after the split.

Initial integration should use a local source checkout or Git worktree plus
project references while the API contract is still moving quickly. After the
first extraction stabilizes, publish the Runtime as versioned artifacts:

- `CodeIsland.Contracts` as the public DTO/API contract package.
- `CodeIsland.RuntimeHost` and `CodeIsland.Bridge` as release executables.
- Runtime implementation assemblies as internal build artifacts unless a formal
  SDK task promotes them to public packages.

Do not start with a Git submodule. The HUD should not need Runtime internals
once the display contract is complete, and a submodule would preserve a source
level dependency that this split is trying to remove.

## Repository Boundaries

### `CodeIsland.Runtime`

The Runtime repository owns all CLI-facing and display-contract behavior:

- `src/CodeIsland.Contracts`: REST/WebSocket DTO contract.
- `src/CodeIsland.Core`: hook models, source adapters, event normalization,
  hook response builders, settings, transcript readers, IPC protocol, and
  runtime persistence that is not WPF-specific.
- `src/CodeIsland.Hub`: session/pending state, hook server, source service,
  local REST API, WebSocket publishing, token store, and shared Runtime
  composition.
- `src/CodeIsland.RuntimeHost`: standalone Runtime process.
- `src/CodeIsland.Bridge`: short-lived CLI hook bridge.
- Runtime-facing tests for Core, Bridge, Hub, Contracts, and RuntimeHost.
- Runtime-facing docs: `runtime-display-contract.md`,
  `external-display-client.md`, and display-client samples.
- Publish and packaging pieces required to produce RuntimeHost and Bridge
  artifacts.

Runtime must remain local-only by default. Binding to LAN addresses, pairing,
or browser CORS support requires a separate security task.

### Windows HUD Repository

The Windows HUD repository owns presentation and Windows desktop behavior:

- `src/CodeIsland.WpfApp`: WPF windows, controls, view models, tray, hotkeys,
  terminal activation, update checking, sounds, icons, settings UI, and HUD
  animation.
- Windows-only assets and release metadata for the HUD application.
- HUD tests and manual verification scripts when a WPF test project exists.
- HUD packaging that consumes Runtime artifacts instead of compiling Runtime
  internals after the transition.

The HUD may start an embedded Runtime during the transition, but HUD state and
actions must cross the Runtime REST/WebSocket boundary. New display behavior
must not read Hub/Core internals directly.

## Current Dependency Map

Current source dependencies before the repository split:

```text
CodeIsland.Bridge -> CodeIsland.Core
CodeIsland.Hub -> CodeIsland.Contracts + CodeIsland.Core
CodeIsland.RuntimeHost -> CodeIsland.Hub + CodeIsland.Core
CodeIsland.WpfApp -> CodeIsland.Hub + CodeIsland.Core
samples/external-display-console -> public REST/WebSocket only
```

The intended end state for the HUD is:

```text
CodeIsland.WpfApp -> CodeIsland.Contracts
CodeIsland.WpfApp -> Runtime executables/artifacts at package or install time
```

`CodeIsland.WpfApp` should no longer compile against `CodeIsland.Hub` or
`CodeIsland.Core` once the split is complete.

## First Migration Inventory

Move these projects to `CodeIsland.Runtime` first:

| Project or path | Target | Notes |
| --- | --- | --- |
| `src/CodeIsland.Contracts` | Runtime repo | Public display contract. Version first. |
| `src/CodeIsland.Core` | Runtime repo | Audit for WPF-only types before extraction. |
| `src/CodeIsland.Hub` | Runtime repo | Runtime state and local API owner. |
| `src/CodeIsland.RuntimeHost` | Runtime repo | Standalone host entrypoint. |
| `src/CodeIsland.Bridge` | Runtime repo | CLI hook bridge executable. |
| `tests/CodeIsland.Core.Tests` | Runtime repo | Moves with Core. |
| `tests/CodeIsland.Bridge.Tests` | Runtime repo | Moves with Bridge. |
| `tests/CodeIsland.Hub.Tests` | Runtime repo | Moves with Hub. |
| `docs/runtime-display-contract.md` | Runtime repo | Canonical display contract. |
| `docs/external-display-client.md` | Runtime repo | Runtime client quickstart. |
| `samples/external-display-console` | Runtime repo | Executable contract sample. |

Keep these in the Windows HUD repository:

| Project or path | Owner | Notes |
| --- | --- | --- |
| `src/CodeIsland.WpfApp` | HUD repo | Presentation and Windows shell integration. |
| `imgs/` | HUD repo | Product screenshots and README assets. |
| HUD release packaging | HUD repo | Can bundle Runtime artifacts after split. |
| HUD settings UI | HUD repo | Calls Runtime API for source/runtime operations. |

## Dependencies To Decouple First

These are the blocking references that prevent the HUD from becoming a pure
display client:

1. `src/CodeIsland.WpfApp/CodeIsland.WpfApp.csproj` references
   `CodeIsland.Hub` and `CodeIsland.Core`.
2. `IWpfRuntimeClient` exposes `HubStateChangedEventArgs`, which lives in
   `CodeIsland.Hub`.
3. `WpfRuntimeApiClient` maps public DTOs back into `SessionSnapshot`,
   `PermissionRequest`, `QuestionData`, `SideEffect`, and
   `HubPendingActionSnapshot` from Runtime internals.
4. WPF view models use Core models such as `SessionSnapshot`, `AgentStatus`,
   `PermissionRequest`, `QuestionData`, and source metadata.
5. Settings and source management UI still depends on `ICodeIslandSourceService`
   from Hub rather than a HUD-local API-facing abstraction.
6. Terminal activation still depends on Runtime session model shapes instead of
   a HUD-local projection DTO.

The first HUD decoupling task should introduce HUD-owned projection records and
events, then map `CodeIsland.Contracts` DTOs directly into those records. After
that, `IWpfRuntimeClient` can stop referencing Hub/Core types.

## Version And Contract Strategy

Treat `CodeIsland.Contracts` as the display-client compatibility boundary:

- Public DTO changes are additive by default.
- Removing or renaming a public field requires a replacement field, capability
  flag, or endpoint version.
- RuntimeHost and Bridge versions should follow the same release version for a
  given Runtime release.
- The Windows HUD records the minimum compatible Runtime contract version and
  refuses or degrades gracefully when capabilities are missing.
- WebSocket clients must tolerate unknown event types and refetch REST snapshots
  after reconnect.

Initial packages should be file or local feed based for development. Public
NuGet publishing is out of scope until the contract and release process settle.

## Migration Phases

### Phase 0: Freeze The Boundary

- Keep `docs/runtime-display-contract.md` as the contract source of truth.
- Include `CodeIsland.RuntimeHost` in solution builds.
- Keep `samples/external-display-console` free of project references to WPF,
  Hub, Core, Bridge, and transcript files.
- Validate that RuntimeHost can run independently and the HUD can connect
  through `runtime_launch_mode=external`.

### Phase 1: Remove HUD Compile-Time Runtime Internals

- Add HUD-local projection types for sessions, pending actions, permissions,
  questions, source status, and side effects.
- Change `IWpfRuntimeClient.StateChanged` to expose HUD-local event args.
- Change `WpfAppState` and HUD view models to depend on HUD projection types.
- Keep Runtime API snapshots and WebSocket events as the only source of Runtime
  state in HUD command paths.
- Remove `CodeIsland.Hub` and `CodeIsland.Core` project references from
  `CodeIsland.WpfApp` when the build is clean.

### Phase 2: Extract Runtime Source

- Create `CodeIsland.Runtime` from the Runtime-owned projects and tests.
- Keep namespaces stable during the first move to reduce diff noise.
- Move Runtime docs and the external display console sample with the Runtime.
- Keep the current repository building against a local Runtime checkout or
  local package feed.
- Verify the bridge executable path repair flow against the new layout.

### Phase 3: Switch HUD To Runtime Artifacts

- Consume `CodeIsland.Contracts` from the Runtime artifact source.
- Bundle `CodeIsland.RuntimeHost.exe` and `CodeIsland.Bridge.exe` in HUD
  packages or require an installed Runtime, depending on the release channel.
- Keep `runtime_launch_mode=embedded` only if an embedded library artifact is
  intentionally supported. Otherwise, move to `external` as the normal mode.
- Update installer and ZIP scripts so HUD and Runtime artifact versions are
  visible and testable.

### Phase 4: Independent Release Cadence

- Runtime releases publish contract docs, RuntimeHost, Bridge, and optional
  contract packages.
- HUD releases declare the Runtime version or contract capability set they
  require.
- Add compatibility tests that run the HUD client against a RuntimeHost built
  from the declared compatible Runtime release.

## Rollback Strategy

Each phase must keep a short rollback path:

- Phase 0 rollback: remove documentation or solution metadata changes.
- Phase 1 rollback: keep old Hub/Core references until the HUD-local projection
  layer is complete; do not delete old paths in the same PR that introduces the
  projection.
- Phase 2 rollback: keep the pre-extraction repository branch untouched and use
  a local source checkout until package consumption is proven.
- Phase 3 rollback: fall back to the previous bundled Runtime artifacts or the
  last known good local feed package.

Do not combine source extraction, package consumption, installer changes, and
HUD runtime-mode defaults in one PR.

## Validation Commands

Run these checks during each migration phase:

```powershell
dotnet build CodeIsland.slnx
dotnet test
dotnet build samples/external-display-console/ExternalDisplay.Sample.csproj
dotnet run --project samples/external-display-console -- --help
dotnet run --project src/CodeIsland.RuntimeHost -- --token dev-token --port 32145 --no-repair
```

Before packaging changes:

```powershell
.\scripts\publish-single-file.ps1 -Runtime win-x64
.\scripts\create-release-zip.ps1 -Runtime win-x64
```

If installer changes are included and Inno Setup is available:

```powershell
.\scripts\create-installer.ps1 -Runtime win-x64 -SkipPublish
```

Manual verification:

- Start RuntimeHost.
- Set the HUD to `runtime_launch_mode=external`.
- Verify health, sessions, pending actions, permission allow/deny,
  current-question answer, dismiss, source status, and WebSocket reconnect.
- Verify hook install/repair still points CLI hooks at the expected Bridge
  executable.

## Risks

- HUD can appear decoupled while still compiling against Runtime internals.
  Treat remaining `CodeIsland.Hub` and `CodeIsland.Core` references in WPF as
  blocking work.
- RuntimeHost and Bridge asset paths can drift after extraction. Keep repair
  tests and packaging checks close to any layout change.
- Contract docs can drift from DTOs. Any change to `CodeIsland.Contracts`,
  `CodeIslandApiHost`, or WebSocket event types must update
  `docs/runtime-display-contract.md`.
- Local API security assumptions can expand accidentally. Keep the first split
  localhost-only.
- Two repositories can drift if source copying is used for too long. Move from
  local source checkout to versioned artifacts once the first extraction works.

## Follow-Up Trellis Tasks

1. HUD projection decoupling from Hub/Core compile-time types.
2. Runtime repository skeleton and source extraction.
3. Runtime artifact publishing for Contracts, RuntimeHost, and Bridge.
4. HUD packaging consumption of Runtime artifacts.
5. Runtime/HUD compatibility test matrix.
