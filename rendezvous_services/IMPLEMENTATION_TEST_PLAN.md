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
- In progress (core + initial hardening/tests complete as of 2026-07-07)

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
  - Verified passing test run: `uv run python -m unittest discover -s server/tests -v` (16 tests passed).

Remaining to close Phase 1
- See the open items in the **Phase 1 completion checklist** below.
- Current outstanding checklist entries:
  - `Add duplicate ID/re-registration behavior tests for host/client reconnection semantics.`
  - `Add direct-success arbitration assertions (ensure no use_relay emitted after successful punch).`
  - `Run local multi-process websocket simulation and record results.`

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
- [x] Validate pinned runtime setup and run server tests successfully (`16` passing).
- [ ] Add duplicate ID/re-registration behavior tests for host/client reconnection semantics.
- [ ] Add direct-success arbitration assertions (ensure no `use_relay` emitted after successful punch).
- [ ] Run local multi-process websocket simulation and record results.

### Phase 2: Relay Server
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

### Phase 3: NetKeyer App Integration
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
- Complete remaining Phase 1 exit checks, then begin Phase 2 relay implementation in [rendezvous_services/relay/relay.py](rendezvous_services/relay/relay.py).
