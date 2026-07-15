# Remote Keying Feature changes

## 2026-07-15

### Changed
- Remote client mapped-direct retry now uses a short timeout before relay fallback to avoid long OS TCP timeout delays in rendezvous negotiation.
- Relay pending-session timeout default increased from 10 seconds to 30 seconds in:
  - [rendezvous_services/relay/relay.py](rendezvous_services/relay/relay.py)
  - [rendezvous_services/docker-compose.yml](rendezvous_services/docker-compose.yml)

### Improved
- Host authentication failure logging now clearly indicates connection refusal due to shared token mismatch in [Services/Remote/RemoteClientSession.cs](Services/Remote/RemoteClientSession.cs).
- Client diagnostics are now clearer for rapid auth-refusal disconnects:
  - Always-on host error payload logging in [Services/Remote/RemoteClientService.cs](Services/Remote/RemoteClientService.cs).
  - Client status guard preserves explicit host error messages instead of immediately replacing them with generic EOF disconnect text.

### Troubleshooting
- Verified field behavior during WAN validation:
  - Shared token mismatch causes host-side connection refusal.
  - Host firewall policy (especially Windows Public profile inbound rules) can prevent direct and mapped-direct TCP success even when automatic mapping reports success.
  - Relay fallback remains functional when direct paths are blocked.

## 2026-07-08

### Added
- Three-stage rendezvous connection negotiation flow:
  - Initial direct transport attempt to rendezvous-provided host endpoint.
  - Automatic host port mapping request phase (`request_port_map` / `port_map_result`) using UPnP first, then NAT-PMP.
  - Deterministic relay fallback only when direct and mapped-direct attempts fail.
- Host-side automatic TCP mapping helper:
  - [Services/Remote/IHostPortMapper.cs](Services/Remote/IHostPortMapper.cs)
  - [Services/Remote/HostPortMapper.cs](Services/Remote/HostPortMapper.cs)

### Changed
- Rendezvous server protocol and orchestration now support mapping negotiation before relay fallback:
  - Added message handling/types in [rendezvous_services/server/models.py](rendezvous_services/server/models.py):
    - `request_port_map` (client->server and server->host)
    - `port_map_result` (host->server)
  - Added mapping-aware session state transitions in [rendezvous_services/server/state.py](rendezvous_services/server/state.py).
  - Updated orchestration in [rendezvous_services/server/websocket_handlers.py](rendezvous_services/server/websocket_handlers.py) to:
    - request host mapping after direct timeout/fail,
    - emit updated `host_endpoint` on mapping success,
    - fall back to `use_relay` when mapping is unavailable or fails.
- NetKeyer rendezvous client/host integration now includes map-request and mapped-endpoint retry handling:
  - [Services/Rendezvous/RendezvousControlModels.cs](Services/Rendezvous/RendezvousControlModels.cs)
  - [Services/Rendezvous/IRendezvousControlService.cs](Services/Rendezvous/IRendezvousControlService.cs)
  - [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs)
  - [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs)
- Connection success logging is now explicit and always-on for operational visibility:
  - [Services/Remote/RemoteClientService.cs](Services/Remote/RemoteClientService.cs)
  - [Services/Remote/RemoteHostService.cs](Services/Remote/RemoteHostService.cs)
  - Added transport labels in connection success logs: `direct`, `mapped-direct`, `relay`.
- CW operating controls now persist in settings only for Remote Client mode:
  - `CwSpeed`, `SidetoneVolume`, and `CwPitch` are saved/restored for client mode sessions.
  - Host and standalone flows remain radio-driven and do not persist these values locally.
- Setup and operating UI updates in [Views/MainWindow.axaml](Views/MainWindow.axaml) + [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs):
  - Rendezvous input changed from a single URL field to separate `Redezvous Server` and `Port` fields.
  - Rendezvous URL is now generated in code as `http://<server>:<port>` (default port `49923`) and legacy saved URL values are parsed into the new fields.
  - Setup label copy refined:
    - `Redezvous Server` renamed to `ID Server`.
    - Client-side `Selected Host ID` renamed to `Host ID`.
  - Spinner arrows removed from both setup `Port` input controls for the ID server endpoint fields.
  - Remote mode selection labels simplified to `Remote Client` and `Remote Host` (removed `(Computer #1)` / `(Computer #2)`).
  - Operating page section labels renamed to `Host Status` and `Client Status`.
- UI branding updates across window titles and About dialog:
  - Main and dialog window titles now use `NetKeyer+Remote` branding.
  - About dialog title and app name updated to `NetKeyer+Remote`.
  - About credits updated to:
    - `by Eric NR4O`
    - `forked from NetKeyer by Andrew KC2G and contributors`
  - `Check for Updates` button is intentionally disabled until a new update location is configured.

### Reliability
- Rendezvous server test suite expanded and passing:
  - `uv run python -m unittest discover -s server/tests -v`
  - Result: 23 tests passed.
- WAN validation confirmed mapped-endpoint negotiation path is operational before relay fallback.

## 2026-07-07

### Added
- Rendezvous Phase 1 websocket handler integration tests in [rendezvous_services/server/tests/test_websocket_handlers.py](rendezvous_services/server/tests/test_websocket_handlers.py), including:
  - Connect orchestration signaling checks (`incoming_client`, `host_endpoint`, `start_punch`).
  - Host capacity rejection (`host_full`).
  - Not-registered and unknown-session rejection paths.
  - Timeout fallback signaling (`use_relay`) to both endpoints.
  - Disconnect cleanup validation for session removal and host capacity decrement.
  - Direct-success arbitration validation (no relay fallback after successful punch).
  - Duplicate host/client re-registration replacement semantics.
- Rendezvous runtime pinning/config files:
  - [rendezvous_services/.python-version](rendezvous_services/.python-version) (`3.11`).
  - [rendezvous_services/pyproject.toml](rendezvous_services/pyproject.toml) with pinned Python/dependency ranges.
- Phase 2 relay runtime scaffold in [rendezvous_services/relay/relay.py](rendezvous_services/relay/relay.py):
  - `SESSION <session_id> <role>` handshake parsing.
  - Session pairing and bidirectional byte forwarding.
  - Pending-session timeout garbage collection.
  - Disconnect/error cleanup for both peers.
- Relay test coverage in [rendezvous_services/relay/tests/test_relay.py](rendezvous_services/relay/tests/test_relay.py):
  - Invalid handshake rejection.
  - Pairing/forwarding and reversed connect order.
  - Timeout cleanup and disconnect propagation.
  - Duplicate-role rejection error-text assertion.
  - Sustained relay throughput/stability test.
- Initial Phase 3 NetKeyer rendezvous integration:
  - Added rendezvous signaling service in [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs).
  - Added rendezvous control models/interface in [Services/Rendezvous/RendezvousControlModels.cs](Services/Rendezvous/RendezvousControlModels.cs) and [Services/Rendezvous/IRendezvousControlService.cs](Services/Rendezvous/IRendezvousControlService.cs).
  - Added persisted rendezvous settings in [Models/UserSettings.cs](Models/UserSettings.cs) (`RemoteUseRendezvous`, `RemoteRendezvousServerUrl`, `RemoteRendezvousHostId`).
  - Added rendezvous host discovery (`list_hosts`) support and client-side host selection wiring.
- Completed Phase 3 relay fallback runtime integration:
  - Added relay handshake support in [Services/Remote/RemoteClientService.cs](Services/Remote/RemoteClientService.cs) (`SESSION <session_id> CLIENT`).
  - Added host relay transport dial-out support in [Services/Remote/RemoteHostService.cs](Services/Remote/RemoteHostService.cs) and [Services/Remote/IRemoteHostService.cs](Services/Remote/IRemoteHostService.cs) (`SESSION <session_id> HOST`).
  - Added rendezvous host relay callback wiring in [Services/Rendezvous/RendezvousControlModels.cs](Services/Rendezvous/RendezvousControlModels.cs) and [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs).
- Completed Phase 4 deployment packaging assets:
  - Added [rendezvous_services/server/Dockerfile](rendezvous_services/server/Dockerfile) for rendezvous container runtime.
  - Added [rendezvous_services/relay/Dockerfile](rendezvous_services/relay/Dockerfile) for relay container runtime.
  - Added [rendezvous_services/docker-compose.yml](rendezvous_services/docker-compose.yml) with `relay`, `rendezvous`, and optional `nginx` profile.
  - Added nginx snippets [rendezvous_services/nginx/rendezvous.conf](rendezvous_services/nginx/rendezvous.conf) (WebSocket proxy) and [rendezvous_services/nginx/stream-relay.conf](rendezvous_services/nginx/stream-relay.conf) (TCP stream proxy).

### Changed
- Rendezvous relay default port updated to `49921` in:
  - [rendezvous_services/server/main.py](rendezvous_services/server/main.py)
  - [rendezvous_services/server/websocket_handlers.py](rendezvous_services/server/websocket_handlers.py)
- Websocket hardening in [rendezvous_services/server/websocket_handlers.py](rendezvous_services/server/websocket_handlers.py):
  - Session ownership checks for host/client `punch_result` messages.
  - Explicit `session_mismatch` rejection for invalid host/client/session combinations.
  - Disconnect lifecycle cleanup now closes sessions owned by disconnecting host/client.
- [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now supports opt-in rendezvous-assisted remote startup:
  - Host registers with rendezvous on start.
  - Client resolves host endpoint through rendezvous before direct TCP connect.
  - Client sends initial punch result to rendezvous after connect attempt.
  - Client now defaults to discovered rendezvous host selection during connect, with manual host ID only as fallback.
- [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now performs direct-first remote client connect with automatic relay fallback when rendezvous emits `use_relay`.
- Setup UI in [Views/MainWindow.axaml](Views/MainWindow.axaml) now includes rendezvous URL/host settings and client host discovery refresh/select controls.
- Relay host advertisement for fallback signaling in [rendezvous_services/server/websocket_handlers.py](rendezvous_services/server/websocket_handlers.py) now derives an externally reachable host from websocket request headers when configured relay host is the internal Docker alias (`relay`), allowing host/client apps to use relay fallback without nginx stream proxy.
- Rendezvous runtime diagnostics and host/client status behavior improvements:
  - [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now emits always-on rendezvous startup/connect/fallback diagnostics via `DebugLogger.LogAlways`.
  - [Views/MainWindow.axaml](Views/MainWindow.axaml) + [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now show host waiting status and rendezvous status on the same operating-page line.
  - [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now normalizes IPv4-mapped IPv6 addresses in Host Client Status rows (removes `::ffff:` prefix from displayed IPv4 values).
- Rendezvous container runtime in [rendezvous_services/server/Dockerfile](rendezvous_services/server/Dockerfile) now installs `uvicorn[standard]` to ensure WebSocket upgrade support is present in Docker deployments.
- Relay fallback timing in [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now caps rendezvous direct-connect attempts to 2 seconds before fallback to avoid relay pairing timeout churn and improve host/client relay session convergence.

### Reliability
- Rendezvous state cleanup improved in [rendezvous_services/server/state.py](rendezvous_services/server/state.py) via `close_sessions_for_host` and `close_sessions_for_client` helpers to prevent stale session/capacity leakage.
- Phase 1 server tests executed successfully with pinned runtime:
  - `uv run python -m unittest discover -s server/tests -v`
  - Result: 19 tests passed.
- Local route-level websocket simulation validated expected host-list and connect orchestration message flow through `/ws/host` and `/ws/client`.
- Relay validation completed:
  - `uv run python -m unittest discover -s relay/tests -v` (8 passed).
  - `uv run python -m unittest discover -v` (26 passed, including relay + server).
- NetKeyer relay integration validation completed:
  - `dotnet build NetKeyer.csproj` succeeded after relay fallback transport updates.
- Docker-hosted rendezvous/relay runtime validation:
  - Host registration and client host discovery verified against running Docker services.
  - Relay fallback path verified operational end-to-end (host/client keying data confirmed through relay transport).

## 2026-06-29

### Added
- Rendezvous Phase 0 contract artifacts under `rendezvous_services`:
  - Protocol contract freeze document and sequence-flow documentation.
  - Versioned JSON schemas for host, client, and server message sets.
  - Initial server-side Pydantic message models and endpoint-specific validators.
  - Schema-conformance tests for required fields, constraints, and endpoint message routing.

### Changed
- Remote telemetry lag measurement corrected for cross-system clock skew by keeping raw apparent-age values and normalizing to a per-client baseline before UI/log reporting.
- Telemetry labels clarified to distinguish normalized lag metrics in logs while keeping concise labels in UI.

### Improved
- Remote telemetry entries are now logged by default under `remote-telemetry` without requiring NETKEYER_DEBUG category configuration.
- Telemetry display upgraded to high-contrast bold magenta for improved visibility.
- Telemetry UI reformatted to two lines to reduce truncation:
  - Line 1: last lag, avg lag, max lag
  - Line 2: accepted 60s, stale
  - Second line aligned after the telemetry label colon.

### Reliability
- Telemetry values now track WAN delay variation more accurately (instead of appearing as persistent zeros under clock-offset conditions).

## 2026-06-24 (Phase 1)

### Added
- Initial Remote Client/Remote Host transport foundation over TCP with length-prefixed JSON frames.
- Remote protocol envelope and message set: `hello`, `auth`, `heartbeat`, `paddleState`, `disconnect`, `error`.
- Setup page remote-mode controls for Standalone, Remote Client, and Remote Host operation.
- Remote host settings for bind address, listen port, shared token, and maximum client count.

### Changed
- Input pipeline extended so client-mode paddle/straight/PTT events are transmitted remotely to host.
- Host-mode keying path integrated to consume remote paddle events and drive keying controller.

### Reliability
- Default remote port standardized to `49920`.
- Incremental build-and-test cycles used to validate remote integration after each change set.

## 2026-06-25 (Phase 1)

### Added
- Two-machine LAN smoke-test validation workflow and execution tracking.
- Host/client identity fields in remote setup and status views (callsign and host name).
- Host-side client status/history view with IP, callsign, connection status, and last active timestamp.

### Changed
- Host-mode sidetone behavior refined so local host mute does not force SmartSDR/Flex monitor gain mute path.
- Remote status UX expanded for clearer operating-state visibility in setup and operating pages.

### Improved
- Native shim and managed build validation completed successfully on this environment.
- LAN reachability and baseline remote keying path validated in host/client workflow.

## 2026-06-26

### Added
- Remote Host active-client ownership lock to prevent simultaneous multi-client keying contention.
- Configurable client ownership hold time in setup UI (`Client Hold Time`), range 0.5s to 30.0s in 0.5s increments.
- Client-mode operating `Client Host Status` block with host IP, host name, and compact status row.
- Host-mode and client-mode compact telemetry summary lines in operating view.
- Heartbeat telemetry path from host to client so client-side telemetry displays live host metrics.

### Changed
- Setup page button layout updated to place `Exit` on the same row as `Connect` and `Connect by IP...`.
- Main window dimensions reduced and unified across setup and operating windows.
- Host/client status display formatting refined (IP normalization and concise status text for client view).
- Operating status bar contrast updated for readability in both light and dark desktop themes.

### Improved
- Reconnect identity handling improved to reduce duplicate client rows in host history display.
- Host client history behavior improved with connected-first sorting and stale disconnected entry cleanup.
- Host telemetry now includes:
  - Last lag
  - Avg lag
  - Max lag over the last 60 seconds
  - Accepted frames in the last 60 seconds
  - Stale dropped frame count
- Rolling 60-second telemetry metrics now age out during idle periods (for example, `accepted 60s` returns to 0 when no frames are received in the window).

### Reliability
- Stale-frame drop policy added on host receive path to reject delayed paddle frames before keying.
- Ongoing successful `dotnet build` validation maintained after each remote-feature change set.
