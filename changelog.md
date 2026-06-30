# Remote Keying Feature changes

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
