# NetKeyer Rendezvous/Relay Implementation and Testing Plan

## Scope
Implement a production-ready rendezvous control service and relay fallback service based on the specification in [rendezvous_services/NetKeyer-Rendezvous-specification.txt](rendezvous_services/NetKeyer-Rendezvous-specification.txt), then integrate host/client behavior in the NetKeyer app and validate end-to-end connectivity.

## Runtime Setup

This service is pinned to Python 3.11 for deterministic local and CI behavior.

Runtime and dependency configuration files:
- [rendezvous_services/.python-version](rendezvous_services/.python-version) (`3.11`)
- [rendezvous_services/pyproject.toml](rendezvous_services/pyproject.toml) (`requires-python = ">=3.11,<3.12"`)

Setup steps (Windows PowerShell):
```powershell
Set-Location "C:\GitHub\NetKeyer\rendezvous_services"
uv venv --python 3.11 .venv
uv sync --no-install-project
```

Run tests:
```powershell
Set-Location "C:\GitHub\NetKeyer\rendezvous_services"
uv run python -m unittest discover -s server/tests -v
```

Notes:
- The project uses `tool.uv.package = false` to avoid editable-build failures for this app-style layout.
- If `python`/`python3` on PATH resolve to Windows Store aliases, continue using `uv run` or `.venv\\Scripts\\python.exe` directly.

## Goals
- Enable host/client discovery and pairing through a WebSocket rendezvous service.
- Attempt direct TCP hole punching first, with deterministic relay fallback.
- Keep control-plane protocol strict and observable.
- Validate behavior across LAN and WAN-like network conditions.

## Non-Goals (Initial Increment)
- Persistent host registry (Redis) beyond process lifetime.
- Multi-region routing and geo load balancing.
- AuthN/AuthZ hardening beyond basic identifiers and optional shared-token controls.

## Architecture Workstreams
1. Rendezvous server (FastAPI + WebSockets).
2. Relay server (asyncio raw TCP pipe).
3. NetKeyer client/host integration for registration, discovery, connect orchestration, punch, fallback.
4. Docker + nginx deployment configuration.
5. Observability and test harness.

## Phase Plan

### Phase 0: Design Freeze and Contracts
Status
- Complete (2026-06-29)

Deliverables
- Message contract document (source of truth) for all JSON messages.
- Validation rules and error semantics.
- Sequence diagrams for:
  - Host register/listen
  - Client register/list/select/connect
  - Punch success
  - Punch timeout and relay fallback

Implementation notes
- Define Pydantic models for all message types.
- Add explicit protocol version field where practical for future migration.

Exit criteria
- Contract reviewed and no ambiguous fields.
- Error handling and unknown message behavior documented.

Completion summary (2026-06-29)
- Added protocol contract and flow documentation:
  - [rendezvous_services/PHASE0_PROTOCOL_CONTRACT.md](rendezvous_services/PHASE0_PROTOCOL_CONTRACT.md)
  - [rendezvous_services/PHASE0_SEQUENCE_FLOWS.md](rendezvous_services/PHASE0_SEQUENCE_FLOWS.md)
- Added JSON message schemas:
  - [rendezvous_services/server/schemas/host_messages.json](rendezvous_services/server/schemas/host_messages.json)
  - [rendezvous_services/server/schemas/client_messages.json](rendezvous_services/server/schemas/client_messages.json)
  - [rendezvous_services/server/schemas/server_messages.json](rendezvous_services/server/schemas/server_messages.json)
- Added versioned Pydantic contract models and validators:
  - [rendezvous_services/server/models.py](rendezvous_services/server/models.py)
- Added schema-conformance model tests:
  - [rendezvous_services/server/tests/test_models.py](rendezvous_services/server/tests/test_models.py)

### Phase 1: Rendezvous Server Core
Status
- Complete (2026-07-07)

Deliverables
- [rendezvous_services/server/main.py](rendezvous_services/server/main.py): app bootstrap and routes.
- [rendezvous_services/server/state.py](rendezvous_services/server/state.py): in-memory state and session maps.
- [rendezvous_services/server/websocket_handlers.py](rendezvous_services/server/websocket_handlers.py): /ws/host and /ws/client logic.
- [rendezvous_services/server/models.py](rendezvous_services/server/models.py): Pydantic message models.

Functional requirements
- Host lifecycle:
  - register_host accepted and stored with public endpoint and metadata.
  - host removal on disconnect.
- Client lifecycle:
  - register_client accepted and stored.
  - list_hosts returns connected hosts only.
  - connect_request creates session and signals both parties.
- Session orchestration:
  - send incoming_client to host.
  - send host_endpoint to client.
  - send start_punch(session_id) to both.
  - on punch_result success/fail arbitration:
    - success by either side marks session established.
    - fail timeout path triggers use_relay to both.

State management details
- Maintain:
  - hosts map
  - clients map
  - sessions map keyed by session_id
- Add TTL cleanup for stale sessions (for robustness).
- Add lock discipline for concurrent websocket updates.

Exit criteria
- Local multi-process simulation validates host list and connect orchestration.
- Session cleanup verified on disconnect and timeout.

Progress summary (2026-07-07)
- Implemented rendezvous runtime core:
  - FastAPI app with `/health`, `/ws/host`, and `/ws/client` endpoints.
  - In-memory host/client/session state with lock discipline and TTL sweeper.
  - Host/client websocket message validation and orchestration flow.
- Implemented hardening updates:
  - Default relay port set to `49921`.
  - Endpoint identity checks (`host_mismatch`, `client_mismatch`).
  - Session ownership checks on `punch_result` (`session_mismatch`).
  - Unknown/not-registered rejection paths.
  - Disconnect-triggered session cleanup to prevent stale capacity usage.
- Test coverage added and validated:
  - Contract/model validation tests in [rendezvous_services/server/tests/test_models.py](rendezvous_services/server/tests/test_models.py).
  - Handler behavior tests in [rendezvous_services/server/tests/test_websocket_handlers.py](rendezvous_services/server/tests/test_websocket_handlers.py).
  - Verified passing test run: `uv run python -m unittest discover -s server/tests -v` (19 tests passed).
  - Verified local route-level websocket simulation via `fastapi.testclient`:
    - Host list returned registered host metadata/capacity.
    - Connect orchestration emitted expected message sequence:
      - host: `incoming_client`, `start_punch`
      - client: `host_endpoint`, `start_punch`

Remaining to close Phase 1
- No open items. See completed checklist below.

Phase 1 completion checklist
- [x] Scaffold rendezvous runtime core files (`main.py`, `state.py`, `websocket_handlers.py`).
- [x] Implement host/client websocket registration and endpoint routing.
- [x] Implement connect orchestration signaling (`incoming_client`, `host_endpoint`, `start_punch`).
- [x] Implement punch timeout watchdog and relay fallback signaling (`use_relay`).
- [x] Add session TTL sweeping and lock discipline for concurrent state updates.
- [x] Add endpoint identity checks (`host_mismatch`, `client_mismatch`).
- [x] Add session ownership checks for `punch_result` (`session_mismatch`).
- [x] Add disconnect-driven session cleanup to prevent stale capacity leakage.
- [x] Add/expand unit tests for message validation and handler flows.
- [x] Validate pinned runtime setup and run server tests successfully (`19` passing).
- [x] Add duplicate ID/re-registration behavior tests for host/client reconnection semantics.
- [x] Add direct-success arbitration assertions (ensure no `use_relay` emitted after successful punch).
- [x] Run local multi-process websocket simulation and record results.

### Phase 2: Relay Server
Status
- Complete (2026-07-07)

Deliverables
- [rendezvous_services/relay/relay.py](rendezvous_services/relay/relay.py).

Functional requirements
- Accept line-based handshake: SESSION <session_id> <role>.
- Pair HOST and CLIENT sockets by session_id.
- Start bidirectional forwarding when both peers are present.
- Close both peers cleanly on disconnect/error.
- Garbage-collect incomplete sessions on timeout.

Exit criteria
- Byte-for-byte pipe verified with integration tests.
- Correct behavior for reversed connect ordering (client first or host first).

Progress summary (2026-07-07)
- Implemented [rendezvous_services/relay/relay.py](rendezvous_services/relay/relay.py) with:
  - Line-based handshake parsing: `SESSION <session_id> <role>`.
  - Session pairing by `session_id` and role (`HOST`/`CLIENT`).
  - Bidirectional byte forwarding once both peers are connected.
  - Duplicate-role rejection for an already-occupied session role.
  - Pending-session timeout watchdog cleanup.
  - Disconnect/error propagation that closes both peers and removes session state.
- Added relay tests in [rendezvous_services/relay/tests/test_relay.py](rendezvous_services/relay/tests/test_relay.py):
  - Invalid handshake rejection.
  - Pairing and byte-for-byte bidirectional forwarding.
  - Reversed connect ordering.
  - Incomplete session timeout cleanup.
  - Disconnect propagation and session cleanup.
  - Duplicate-role rejection with explicit error-text assertion.
  - Sustained relay throughput/stability validation.
  - Automated fallback-style relay transport payload exchange validation (`test_fallback_session_style_payload_exchange`).
- Validation:
  - Relay-only tests: `uv run python -m unittest discover -s relay/tests -v` (8 passed).
  - Full suite: `uv run python -m unittest discover -v` (26 passed).

Remaining to close Phase 2
- No open items.

### Phase 3: NetKeyer App Integration
Status
- Complete (2026-07-07)

Deliverables
- New rendezvous-control service in NetKeyer (C#) for host/client modes.
- UI wiring for:
  - Rendezvous server URL
  - Host listing and selection
  - Session status transitions (registering, punching, direct, relay)

Host mode behavior
- Register host with max_clients and metadata.
- Handle incoming_client/start_punch/use_relay signals.
- Execute punch strategy against client endpoint.
- Publish punch_result.

Client mode behavior
- Register client.
- list_hosts and select host.
- Send connect_request.
- Execute punch strategy against host endpoint.
- Publish punch_result.

Connection strategy
- Attempt direct punch for 2s.
- On timeout/fail, switch to relay endpoint and session_id.
- Preserve existing keying payload framing once transport established.

Exit criteria
- End-to-end connect from UI with direct path when possible.
- Automatic relay fallback when direct path fails.

Progress summary (2026-07-07)
- Added initial NetKeyer rendezvous signaling service under [Services/Rendezvous](Services/Rendezvous):
  - [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs)
  - [Services/Rendezvous/RendezvousControlModels.cs](Services/Rendezvous/RendezvousControlModels.cs)
  - [Services/Rendezvous/IRendezvousControlService.cs](Services/Rendezvous/IRendezvousControlService.cs)
- Integrated opt-in rendezvous flow into [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs):
  - Host mode can register presence to rendezvous (`register_host`) during remote-host startup.
  - Client mode can resolve host endpoint via rendezvous (`register_client` + `connect_request`) before TCP connect.
  - Client reports initial `punch_result` success/failure after direct TCP connect attempt.
  - Rendezvous sessions are cleaned up alongside existing remote service stop flow.
- Added rendezvous host discovery flow:
  - Service support for `list_hosts` in [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs).
  - Client setup command and state in [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) to refresh/select discovered hosts.
  - Setup-page rendezvous controls in [Views/MainWindow.axaml](Views/MainWindow.axaml) for URL, discovery refresh, and host selection.
- Finalized rendezvous-first client connect behavior in [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs):
  - Client connect now prefers selected/discovered rendezvous host.
  - If discovery is empty, manual host ID is used as fallback.
  - If neither is available, connection fails with actionable guidance.
- Added persisted rendezvous settings in [Models/UserSettings.cs](Models/UserSettings.cs):
  - `RemoteUseRendezvous`
  - `RemoteRendezvousServerUrl`
  - `RemoteRendezvousHostId`
- Validation:
  - `dotnet build NetKeyer.csproj` succeeded.

Completion updates (2026-07-07)
- Completed relay fallback transport integration in NetKeyer runtime:
  - Client flow in [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now attempts direct connect first, reports `punch_result=false` on direct failure, waits for rendezvous `use_relay`, and reconnects using relay endpoint/session handshake.
  - Client transport in [Services/Remote/RemoteClientService.cs](Services/Remote/RemoteClientService.cs) now emits relay preamble handshake before framed protocol traffic:
    - `SESSION <session_id> CLIENT`
  - Host rendezvous signaling in [Services/Rendezvous/RendezvousControlService.cs](Services/Rendezvous/RendezvousControlService.cs) now handles `use_relay` and forwards relay endpoint/session events to app runtime callbacks.
  - Host transport runtime in [Services/Remote/RemoteHostService.cs](Services/Remote/RemoteHostService.cs) now supports outbound relay session connection and host relay preamble handshake:
    - `SESSION <session_id> HOST`
  - Host integration in [ViewModels/MainWindowViewModel.cs](ViewModels/MainWindowViewModel.cs) now opens relay transport sessions when rendezvous instructs relay fallback.
- Validation:
  - `dotnet build NetKeyer.csproj` succeeded after relay fallback integration.

Remaining to close Phase 3
- No open items.

Phase 3 completion checklist
- [x] Add C# rendezvous control service and session models for host/client registration/connect/list flows.
- [x] Integrate rendezvous settings and host discovery UI wiring in NetKeyer setup flow.
- [x] Implement rendezvous-first client host resolution with manual host ID fallback.
- [x] Report client punch outcome (`punch_result`) to rendezvous control-plane.
- [x] Add relay fallback transport path in client runtime after direct connect failure.
- [x] Add relay handshake support in remote transport (`SESSION <session_id> <role>`).
- [x] Add host-side relay session handling on rendezvous `use_relay` signaling.
- [x] Validate integration with successful `dotnet build NetKeyer.csproj`.

### Phase 4: Deployment Packaging
Deliverables
- [rendezvous_services/server/Dockerfile](rendezvous_services/server/Dockerfile)
- [rendezvous_services/relay/Dockerfile](rendezvous_services/relay/Dockerfile)
- [rendezvous_services/docker-compose.yml](rendezvous_services/docker-compose.yml)
- nginx config snippets for ws upgrade and optional stream proxy.

Exit criteria
- docker compose up starts rendezvous and relay.
- WS and TCP paths verified through nginx.

### Phase 5: Hardening and Observability
Deliverables
- Structured logs (session_id, host_id, client_id, state transitions).
- Metrics counters and timers (Prometheus-friendly):
  - registrations
  - connect requests
  - punch success rate
  - relay fallback rate
  - session durations
- Backpressure limits and message size limits.

Exit criteria
- Can troubleshoot a failed connection from logs alone.
- Basic load soak test passes without leaks.

## Testing Plan

### 1. Unit Tests (Server/Relay)
Rendezvous server
- Message validation per schema.
- Host/client registration and duplicate ID behavior.
- list_hosts filtering (online only).
- connect_request session creation.
- punch_result state transitions.
- Relay fallback trigger logic after timeout.
- Cleanup on ws disconnect.

Relay server
- Handshake parse and invalid handshake rejection.
- Session pairing by id and role.
- Bidirectional forwarding integrity.
- Disconnect propagation and session cleanup.

### 2. Integration Tests (Local)
- Host and client websocket registration flows.
- list_hosts from client returns expected hosts.
- connect_request emits correct message sequence:
  - incoming_client
  - host_endpoint
  - start_punch
- punch_result success path does not send use_relay.
- punch_result fail path sends use_relay to both endpoints.

### 3. End-to-End Transport Tests (NetKeyer + Services)
- Direct path success scenario.
- Forced direct-fail scenario triggers relay.
- Relay data path supports sustained keying payload stream.
- Reconnect logic after transient disconnect.
- Max client cap enforcement.

Current automation note (2026-07-07)
- Relay-side forced direct-fail fallback transport behavior is now covered by automated relay tests in [rendezvous_services/relay/tests/test_relay.py](rendezvous_services/relay/tests/test_relay.py), including `test_fallback_session_style_payload_exchange`.

### 4. WAN/NAT Behavior Tests
- Same NAT (hairpin) and different NAT scenarios.
- Simulated symmetric NAT behavior (expect higher relay fallback).
- Latency/jitter injection tests with netem/Clumsy.
- Validate timeout and fallback under packet loss.

### 5. Reliability/Regression
- Long-running soak (4-8h) with repeated connect/disconnect.
- Memory growth checks in rendezvous and relay.
- Session cleanup for dropped peers.
- Verify no stale hosts listed after disconnect.

### 6. Security/Safety Checks
- Reject malformed/oversized JSON messages.
- Enforce role-specific message permissions by endpoint.
- Session_id unpredictability and collision checks.
- Optional shared token checks in connect flow.

## Test Matrix (Minimum)
- OS: Windows, Linux.
- Network: LAN, WAN-like (50-150ms RTT), high-loss profile.
- Modes: Host-only, Client-only, Host+Client with direct, Host+Client with relay fallback.

## Acceptance Criteria
- Client can discover hosts and connect in under 3 seconds median in nominal conditions.
- Direct punch success path works when NAT allows.
- Relay fallback succeeds automatically when direct fails.
- No stale hosts/clients remain after disconnect timeout.
- All critical path tests pass in CI.

## Suggested Task Breakdown
1. Implement message models + websocket handlers.
2. Implement session manager + cleanup timers.
3. Implement relay pairing/forwarding.
4. Add integration tests for control signaling.
5. Integrate NetKeyer host/client rendezvous flow.
6. Add Docker/nginx deployment assets.
7. Execute WAN simulation test suite and tune timeout values.

## Risks and Mitigations
- NAT unpredictability: make relay fallback deterministic and fast.
- Clock/ordering issues: use server-generated session state and idempotent state transitions.
- Resource leaks under churn: add periodic sweeps + connection close guards.
- Protocol drift: keep strict schema validation and versioned contracts.

## Immediate Next Step
- Begin Phase 4 deployment packaging (Docker + compose + nginx proxy wiring).
