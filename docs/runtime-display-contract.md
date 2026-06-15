# CodeIsland Runtime Display Contract

This document defines the intended boundary between the CodeIsland Runtime and any display client. Runtime is the centralized control plane for CodeIsland: it ingests CLI hook activity from configured sources, normalizes session and pending-action state, and exposes that state to one or more display clients. A display client can be the current WPF HUD, a web UI, a mobile app, a hardware display, or a third-party integration.

The current Windows HUD starts or connects to `CodeIsland.RuntimeHost` and consumes Runtime state through the public REST/WebSocket contract. The Runtime source is being extracted into the independent `CodeIsland-Runtime` repository, while the Windows repository remains the official WPF display client.

Developer entry points:

* `docs/external-display-client.md` explains how to connect a display client, subscribe to events, and send pending-action operations.
* `samples/external-display-console/` is a no-dependency .NET console sample that uses the public REST/WebSocket contract.
* `docs/runtime-repository-split-plan.md` defines the repository split plan for moving Runtime/Hub into an independent Runtime repository.

## Ownership Boundary

### Runtime Owns

The Runtime is the source of truth for all CLI and agent activity:

* Hook ingestion from CLI tools through the bridge and Named Pipe protocol.
* Centralized aggregation of configured CLI sources on the host device, with room for future remote-device source adapters.
* Source-specific hook installation, repair, source capabilities, and runtime assets.
* Source/event normalization and source-specific hook response formatting.
* Session lifecycle state, tool history, recent messages, transcript consumption, and process cleanup.
* Pending permission and question queues.
* Permission/question resolution, including timeout behavior and response JSON written back to the CLI hook.
* Local REST API, WebSocket event stream, auth token, and compatibility capabilities.
* Multi-client fan-out for realtime events. Several display clients may connect at the same time and must observe the same Runtime state.

### Display Clients Own

Display clients are replaceable presentation layers:

* Fetch initial Runtime state through REST.
* Subscribe to Runtime changes through WebSocket.
* Render sessions, pending actions, source status, and runtime assets.
* Send user actions back through REST, such as allow, deny, answer, dismiss, or activate terminal.
* Keep display-local state such as selected item, window position, animation state, theme, density, and sound preferences.
* Optionally manage the lifecycle of a local Runtime process, but only as an operations concern. Display clients must not own Runtime state.

Display clients must not implement hook ingestion, session reducers, pending-action queues, source installation mutation, or source-specific hook response formatting. If a display needs data that is not exposed through the API, the API contract should be extended instead of reading Runtime internals directly.

## Topology Modes

Runtime has two supported topology classes:

* Local managed mode: the official desktop display starts Runtime on `127.0.0.1`, connects to it, and shuts down only the Runtime process that it owns when the display exits.
* Shared remote mode: Runtime is explicitly bound to a non-loopback host such as `0.0.0.0`, allowing several displays on the LAN or other devices to connect with the API token. In this mode, a display may start Runtime for convenience, but it should not shut Runtime down when that display exits because other clients may still be connected.

Runtime must remain localhost-only by default. Remote/mobile access requires explicit user configuration, token authentication, and later pairing/security hardening. Do not silently bind to LAN addresses.

## Data Flow

```text
AI CLI hook
  -> CodeIsland.Bridge
  -> Runtime hook server
  -> Runtime session/pending state
  -> REST snapshots + WebSocket events
  -> Display client
  -> REST action calls
  -> Runtime resolves pending hook response
  -> CodeIsland.Bridge stdout
```

The WPF HUD is currently both a display and the process that may start a managed local Runtime process. That is an operations concern only; WPF must not keep business ownership of hook processing or Runtime state.

## Authentication

`GET /api/health` is unauthenticated and intended for liveness checks.

All other API routes require the local Runtime token. Clients may provide the token in one of these forms:

* `Authorization: Bearer <token>`
* `X-CodeIsland-Token: <token>`
* `?token=<token>` query parameter

The query token form exists for simple WebSocket clients and local tooling. UI clients should prefer headers when possible.

Unauthorized requests return:

```json
{
  "code": "unauthorized",
  "message": "Missing or invalid CodeIsland API token"
}
```

## REST Endpoints

All routes below are under `/api`.

### Runtime

| Method | Path | Purpose | Response |
| --- | --- | --- | --- |
| `GET` | `/health` | Runtime liveness check. No token required. | `ApiHealthDto` |
| `GET` | `/version` | Runtime product/version. | `ApiVersionDto` |
| `GET` | `/capabilities` | Feature flags and compatibility hints. | `ApiCapabilitiesDto` |

### Sources

| Method | Path | Purpose | Response |
| --- | --- | --- | --- |
| `GET` | `/sources` | List supported sources and install status. | `SourceDto[]` |
| `GET` | `/sources/{source}` | Get source support/install status. | `SourceStatusDto` |
| `GET` | `/sources/{source}/status` | Alias for source status. | `SourceStatusDto` |
| `POST` | `/sources/{source}/install` | Install or update hooks for a source. | `SourceOperationResultDto` |
| `POST` | `/sources/{source}/uninstall` | Remove CodeIsland-owned hooks for a source. | `SourceOperationResultDto` |
| `POST` | `/sources/{source}/repair` | Repair hooks for one source. | `SourceOperationResultDto` |
| `POST` | `/sources/repair-all` | Repair all already-installed hook configurations. | `{ success: boolean }` |
| `GET` | `/runtime-assets` | Get deployed Runtime hook script and bridge paths. | `RuntimeAssetsDto` |
| `POST` | `/runtime-assets/repair` | Repair shared Runtime assets. | `{ success: boolean, assets: RuntimeAssetsDto }` |

### Sessions

| Method | Path | Purpose | Response |
| --- | --- | --- | --- |
| `GET` | `/sessions` | List current Runtime sessions. | `SessionDto[]` |
| `GET` | `/sessions/{sessionId}` | Get one session. | `SessionDto` or `404` |
| `GET` | `/sessions/{sessionId}/messages` | Get recent messages for one session. | `ChatMessageDto[]` |
| `POST` | `/sessions/{sessionId}/dismiss` | Remove a session from Runtime state. | `{ success: true }` or `404` |
| `POST` | `/sessions/{sessionId}/activate-terminal` | Request terminal activation for a session. | `{ success: true }` or `404` |

### Pending Actions

| Method | Path | Purpose | Request | Response |
| --- | --- | --- | --- | --- |
| `GET` | `/pending` | List pending permission/question actions. | None | `PendingActionDto[]` |
| `GET` | `/pending/{actionId}` | Get one pending action. | None | `PendingActionDto` or `404` |
| `POST` | `/permissions/{actionId}/allow` | Allow a pending permission. | `PermissionDecisionRequest` | `{ success: true }` or `404` |
| `POST` | `/permissions/{actionId}/deny` | Deny a pending permission. | `PermissionDecisionRequest` | `{ success: true }` or `404` |
| `POST` | `/questions/{actionId}/answer` | Answer a pending question. | `QuestionAnswerRequest` | `{ success: true }` or `404` |
| `POST` | `/questions/{actionId}/answer-current` | Answer only the current step of a pending multi-question flow. | `QuestionCurrentAnswerRequest` | `QuestionCurrentAnswerResultDto` or `404` |
| `POST` | `/questions/{actionId}/dismiss` | Dismiss a pending question. | None | `{ success: true }` or `404` |

`QuestionAnswerRequest.Answers` is preferred for multi-question or keyed answers. `QuestionAnswerRequest.Answer` is the single-answer fallback.
`QuestionCurrentAnswerRequest.Answers` is for HUD-style step-by-step answering: Runtime records the answer under the pending action's current answer key, advances to the next question when one exists, and returns `Resolved = false` until the final answer completes the hook response.

`PermissionRequestDto.ToolInput` is included for display clients that render command/pattern details. Values are JSON-serializable primitives or JSON objects/arrays. Clients should tolerate unknown value shapes.

`SessionDto.TerminalApp` and `SessionDto.TerminalSessionId` are nullable terminal metadata hints. Display clients should prefer `/sessions/{sessionId}/activate-terminal` for the user action and treat these fields as optional display/activation hints.

## WebSocket Events

Clients connect to `/api/events` with the same token rules. The server sends JSON payloads shaped as `HubEventDto`:

```json
{
  "type": "session.updated",
  "timestampUtc": "2026-06-12T00:00:00Z",
  "data": {}
}
```

Known event types:

| Type | Data | Client behavior |
| --- | --- | --- |
| `session.updated` | `SessionDto[]` | Replace or reconcile session list. |
| `session.removed` | `{ sessionId: string }` | Remove the session locally, or refetch `/sessions`. |
| `pending.updated` | `PendingActionDto[]` | Replace or reconcile pending list. |
| `pending.resolved` | `{ actionId: string, pending: PendingActionDto[] }` | Remove resolved action and reconcile pending list. |
| `source.statusChanged` | `SourceOperationResultDto` or `SourceDto[]` | Refetch `/sources` for a normalized source snapshot. |

Display clients must tolerate unknown event types. For forward compatibility, a client should log unknown event types and refetch snapshots only when it understands the event family.
Several WebSocket clients may connect at the same time. Runtime broadcasts every known realtime event to all authorized clients; clients should treat REST snapshots as the recovery source after reconnect.

## DTO Compatibility Rules

The DTOs in `CodeIsland.Contracts` are the public display-client contract.

Rules for future changes:

* Additive nullable fields are allowed.
* Existing field names and meanings should not change without a new capability flag or replacement endpoint.
* Date/time fields exposed to clients must use UTC `DateTimeOffset` values.
* Status-like fields are strings. Clients must handle unknown values without crashing.
* Error responses should use `ApiErrorDto` with a stable machine-readable `code`.
* WebSocket events should carry snapshot-friendly payloads. Clients should be able to recover by refetching REST state after reconnect.

## Current Migration Gaps

These are known gaps between the desired contract and the current codebase:

* WPF still reads transcript updates directly for selected-session refresh. Runtime should own transcript consumption and expose display-ready messages.
* `source.statusChanged` currently has more than one payload shape. Display clients should refetch `/sources` until a normalized event payload is introduced.
* WPF can embed the Runtime library during transition, but its HUD state and actions now consume the Runtime REST/WebSocket contract. Set `runtime_launch_mode=external` to connect the HUD to an already running standalone Runtime host.
* Remote/mobile access is not a default-on feature. The Runtime now has a host binding knob, but pairing, token rotation UX, CORS/browser-hosting policy, and remote-device source ingestion need dedicated follow-up tasks.

## Implementation Sequence

Recommended migration order:

1. Stabilize this contract and treat `CodeIsland.Contracts` as the display boundary.
2. Move hook server implementation from WPF into Runtime/Hub. (Completed for the embedded Named Pipe server.)
3. Collapse duplicated WPF state handling into Runtime-owned state.
4. Introduce source adapter interfaces behind existing static facades.
5. Add an independent Runtime host process.
6. Convert WPF HUD into an API/WebSocket client.
7. Publish display-client docs and minimal sample.
8. Extract Runtime-owned projects into the independent `CodeIsland-Runtime` repository per `docs/runtime-repository-split-plan.md`.
