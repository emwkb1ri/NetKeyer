# NetKeyer Security Plan

This document defines a phased plan to secure the rendezvous and relay services plus the NetKeyer remote host/client protocols while preserving low-latency CW keying.

## Objectives

- Protect control-plane and data-plane traffic against interception and tampering.
- Add strong, scoped authentication and authorization for rendezvous workflows.
- Provide cryptographic peer authentication and end-to-end encryption for keying traffic.
- Maintain low latency suitable for real-time keying.
- Enable staged rollout with backward compatibility and observability.

## Guiding Principles

- Prioritize changes that reduce risk quickly and safely.
- Separate transport security, identity/auth, and end-to-end session crypto concerns.
- Keep relay blind to plaintext payloads.
- Measure latency and reliability at each phase before enforcement.

## Phase 0: Security Baseline and Threat Model

### Goals

- Establish explicit security and latency requirements.
- Define compatibility and rollout constraints.

### Confirmed Requirements (User-Specified)

1. Keying-path latency budget: maximum 5 ms added latency budget for security changes in the keying path.
2. Compatibility target for testing: maintain compatibility with client version 2.1.34 during the controlled test period.
3. Deployment compatibility scope: no compatibility requirement with pre-existing rendezvous deployments beyond support for client version 2.1.34.
4. Security outcomes to prioritize:
   - Prevent easy unauthorized access to rendezvous and relay services.
   - Prevent unauthorized rendezvous operations.
   - Prevent client impersonation.
   - Reduce token theft risk and replay usefulness.
   - Limit denial-of-service impact on rendezvous and relay services.

### Tasks

1. Define threat model and trust boundaries:
   - Passive network interception.
   - Active MITM attempts.
   - Replay and token theft.
   - Relay compromise.
   - Rogue client impersonation.
2. Define latency SLO and acceptance criteria for keying path (for example, crypto overhead budget at p95).
3. Define compatibility matrix across:
   - Existing desktop versions.
   - Existing rendezvous/relay deployments.
   - New secure protocol versions.
4. Define migration flags and deprecation timeline for insecure modes.
5. Document secret/certificate handling standards for development, staging, and production.

### Deliverables

- Threat model doc.
- Security requirements and latency budget.
- Compatibility matrix and phased enforcement timeline.

### Phase 0 Status

- In progress: requirements captured in this document.
- Next required output: initial threat model draft with trust boundaries, attacker capabilities, and mitigations mapped to Phases 1 through 3.

---

## Phase 1: Edge Hardening with Nginx and TLS

### Goals

- Secure external ingress first.
- Remove plaintext internet exposure.

### Tasks

1. Place rendezvous API/WebSocket endpoints behind nginx with TLS termination.
2. Enforce `wss` for external websocket access; disable plain `ws` externally.
3. Decide relay ingress model:
   - Option A: Relay behind nginx stream proxy.
   - Option B: Relay exposed directly with native TLS in relay service.
4. Apply hardened TLS configuration:
   - TLS 1.2+ (prefer TLS 1.3 where supported).
   - Modern cipher suites.
   - OCSP stapling (when applicable).
   - HSTS for HTTPS endpoints.
5. Add edge protections:
   - Rate limits.
   - Connection limits.
   - Request size/time limits.
6. Restrict access to admin/diagnostic endpoints (`/health`, metrics) by network policy or auth.
7. Keep service-to-service network private and non-public.

### Deliverables

- Nginx configuration files for prod/staging.
- TLS certificate provisioning and rotation runbook.
- Updated deployment docs for secure ingress.

### Phase 1 Status

- Started:
  - TLS-first nginx rendezvous ingress configuration added.
  - nginx compose overlay updated for ports 80/443 and certificate mount path.
  - deployment documentation updated with secure overlay startup instructions.
   - PR-2 controls implemented: request guards/rate limits at nginx and restricted `/health` defaults in rendezvous service.
   - PR-2 observability implemented: nginx structured security access logs now expose deny/throttle signals (`403`, `429`, `limit_req`).
- Current operating mode during compatibility window:
  - dual-path operation (legacy direct path for client v2.1.34 testing + secure nginx ingress path for validation).

### Relay Ingress Decision (Phase 1)

- Selected approach: Option A (relay behind nginx stream proxy) is the default implementation path.
- Rationale: fastest secure rollout with centralized ingress control and lower implementation risk.
- Revisit trigger: if measured relay-path overhead exceeds the keying latency budget, evaluate Option B (direct relay exposure with native TLS).

### Relay Latency Validation Gate

Use this gate before closing Phase 1:

1. Measure baseline keying latency using direct relay exposure path (no nginx stream hop).
2. Measure keying latency with relay through nginx stream proxy (`49922`).
3. Compare p50/p95/p99 and worst-case jitter.
4. Acceptance threshold for Option A:
   - additional p95 relay-path overhead is within the Phase 0 budget (<= 5 ms).
5. If threshold is exceeded, open a Phase 1 exception and evaluate Option B.

### Concrete Relay Latency Test Procedure

Run this procedure before marking Phase 1 complete.

#### A. Test prerequisites

1. Use the same host, client build, and network path for all runs.
2. Use client version 2.1.34 for compatibility-window validation.
3. Disable unrelated high-traffic activity on test hosts.
4. Keep CW speed, sidetone settings, and keyer mode identical across runs.
5. Capture at least 300 key events per run (500 preferred).

#### B. Test scenarios

1. Baseline direct relay exposure:
   - client connects through relay direct port 49921 (no nginx stream hop).
2. Option A relay through nginx stream:
   - client connects through nginx relay proxy port 49922.

Relay-only experiment mode (optional but recommended for controlled runs):

- Server: set `RENDEZVOUS_FORCE_RELAY=true`.
- App/client process: set `NETKEYER_FORCE_RELAY_TRANSPORT=true`.
- Purpose: bypass direct and mapped-direct transport attempts so measured path is relay-only.

#### C. Execution steps (for each scenario)

1. Start services and confirm healthy session establishment.
2. Run a warm-up period of 30 seconds (discard data).
3. Perform three measurement runs, each 60 seconds minimum.
    - Recommended helper invocation:
       - `./rendezvous_services/scripts/capture-relay-latency-data.sh --scenario baseline-49921 --run 1 --duration-seconds 60`
       - `./rendezvous_services/scripts/capture-relay-latency-data.sh --scenario nginx-49922 --run 1 --duration-seconds 60`
    - Script output includes:
       - per-run metadata and environment snapshot
       - synchronized compose logs for `rendezvous`, `relay`, and `nginx`
       - health probe samples and summary percentiles
       - `keying-latency-notes.csv` for manual keying timing captures
4. During each run, generate repeatable keying patterns:
   - alternating dits and dahs at fixed cadence.
   - short burst sequences to observe jitter behavior.
5. Collect timestamps for key-down to audio/host-action outcome.
6. Save raw run data with scenario label and timestamp.

#### D. Metrics to compute

1. p50 latency (ms).
2. p95 latency (ms).
3. p99 latency (ms).
4. Max latency (ms).
5. Jitter proxy: p99 minus p50 (ms).

Compute delta against baseline:

- delta_p95 = p95_option_a - p95_baseline
- delta_p99 = p99_option_a - p99_baseline

#### E. Pass/fail criteria

1. Primary gate: delta_p95 <= 5 ms.
2. Secondary check: no sustained jitter regression that impacts CW usability.
3. Stability check: no session drops or burst-loss anomalies during test windows.

If any gate fails:

1. Open a Phase 1 exception issue.
2. Attach raw measurements and environment details.
3. Evaluate Option B for relay path.

#### F. Results template

| Scenario | Run | Samples | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | p99-p50 (ms) | Notes |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| Baseline (49921) | 1 |  |  |  |  |  |  |  |
| Baseline (49921) | 2 |  |  |  |  |  |  |  |
| Baseline (49921) | 3 |  |  |  |  |  |  |  |
| Option A (49922) | 1 |  |  |  |  |  |  |  |
| Option A (49922) | 2 |  |  |  |  |  |  |  |
| Option A (49922) | 3 |  |  |  |  |  |  |  |

Decision summary fields:

- Baseline aggregate p95:
- Option A aggregate p95:
- delta_p95:
- Gate result (pass/fail):
- Follow-up action:

Companion aggregation helper:

- `./rendezvous_services/scripts/summarize-relay-latency-runs.sh`
- Example:
   - `./rendezvous_services/scripts/summarize-relay-latency-runs.sh --input-root ./rendezvous_services/measurements/relay-latency --budget-ms 5 --output ./rendezvous_services/measurements/relay-latency/report.md`

---

## Phase 2: Rendezvous Authentication and Authorization

### Goals

- Ensure only authorized hosts/clients can register and connect.
- Make authorization explicit and scoped.

### Tasks

1. Add signed access tokens (JWT) with short TTL for control-plane calls.
2. Validate token signature, expiry, issuer/audience, and required claims.
3. Require tokens for:
   - Host registration.
   - Client registration.
   - Connect request.
   - Relay request.
4. Define claims model:
   - Subject identity (`sub`).
   - Role (`host`, `client`, optionally `admin`).
   - Scope (host ID, client ID, account/team as needed).
   - Protocol version compatibility.
5. Add short-lived, one-time connection grant tokens bound to:
   - Session ID.
   - Host ID.
   - Client ID.
   - Expiration.
6. Add anti-replay controls (`jti`, nonce, bounded cache).
7. Keep current shared-token behavior only as migration fallback; schedule removal.

### Deliverables

- Auth validation module and policy docs.
- Token issuance/refresh flow docs.
- Migration toggle for legacy shared-token mode.

---

## Phase 3: End-to-End Encryption and Cryptographic Peer Authentication

### Goals

- Encrypt keying traffic end-to-end regardless of direct or relay path.
- Cryptographically verify host/client identities.

### Tasks

1. Implement secure session handshake:
   - Static identity keys: Ed25519.
   - Ephemeral key exchange: X25519.
   - Key derivation: HKDF-SHA256.
2. Sign handshake transcript with static identity keys.
3. Bind handshake/session to rendezvous-issued connection grant claims.
4. Protect frames with AEAD:
   - ChaCha20-Poly1305 or AES-GCM.
   - Strict sequence number nonce strategy.
5. Add replay/downgrade protection in protocol negotiation.
6. Add secure rekey strategy (time-based or frame-count based).
7. Ensure relay forwards ciphertext only and cannot decrypt payload data.

### Deliverables

- Protocol specification (message schema, state machine, failure behavior).
- Reference implementation in client/host and relay pass-through validation.
- Test vectors for crypto handshake and frame protection.

---

## Phase 4: Transport Behavior and Mode Consistency

### Goals

- Keep security properties consistent across direct and relay operation.
- Prevent insecure fallback in production.

### Tasks

1. Use one encrypted framing model for direct and relay paths.
2. Keep relay behavior transport-agnostic (opaque forwarding of encrypted frames).
3. Implement explicit downgrade refusal by default.
4. Provide controlled debug override flags for local/lab use only.
5. Validate direct-first and relay-fallback switching without changing security posture.
6. Ensure connection errors expose actionable but non-sensitive diagnostics.

### Deliverables

- Unified transport behavior spec.
- Secure default configuration profile.
- Controlled debug/insecure mode policy.

---

## Phase 5: Observability, Validation, and Rollout Enforcement

### Goals

- Safely roll out security changes with measurable confidence.
- Enforce secure defaults once validated.

### Tasks

1. Add security and performance metrics:
   - Handshake duration.
   - Auth failures.
   - Replay rejects.
   - Decrypt/authentication failures.
   - p50/p95 keying latency.
2. Add structured audit events for auth and session lifecycle.
3. Add test coverage:
   - Unit tests for token/claim checks.
   - Integration tests for handshake and encrypted transport.
   - Fault-injection tests for expiry, skew, and relay interruption.
4. Roll out via staged feature flags:
   - `require_wss`.
   - `require_signed_tokens`.
   - `require_e2e_encryption`.
5. Progressively enforce secure-only mode after acceptance thresholds.
6. Publish operational runbooks for incident response and key rotation.

### Deliverables

- Metrics dashboards and alert rules.
- Security test plan and pass criteria.
- Secure-only enforcement checklist.

---

## Rollout Order Recommendation

Recommended implementation order:

1. Phase 0 (requirements and constraints).
2. Phase 1 (nginx + TLS edge hardening).
3. Phase 2 (rendezvous auth and scoped grants).
4. Phase 3 (end-to-end encryption + cryptographic peer auth).
5. Phase 4 (mode consistency and downgrade controls).
6. Phase 5 (observability, staged enforcement, and operations).

This sequence delivers immediate risk reduction, minimizes rework, and supports low-latency validation at each step.

## Open Design Decisions

1. Identity key lifecycle model (user/device enrollment and rotation UX).
2. Token issuer placement (existing app backend/service vs. standalone auth service).
3. Backward compatibility window for legacy clients.
4. Final secure-mode cutover date and enforcement policy.

## Implementation Checklist

Use this checklist to drive implementation in small, reviewable pull requests.

### Phase 1 Kickoff (First PRs)

- [ ] Add nginx TLS termination for rendezvous HTTP/WebSocket endpoints.
- [ ] Redirect or deny plaintext external HTTP/WS access.
- [ ] Restrict `/health` and admin endpoints to private network or authenticated access.
- [ ] Add conservative rate limiting for websocket connect and message burst patterns.
- [ ] Add production-ready TLS settings and certificate path configuration.
- [ ] Update deployment docs with secure ingress topology diagrams and env variable examples.

### Phase 1 Prioritized PR Sequence

#### PR-1: TLS Ingress Baseline (Highest Priority)

Purpose: establish secure external entry and remove plaintext exposure first.

Scope:

- Add nginx TLS termination for rendezvous HTTP/WebSocket traffic.
- Route secure websocket traffic to rendezvous service (`wss` -> internal `ws`).
- Deny or redirect external plaintext HTTP/WS.
- Add certificate path and domain environment variables to deployment config.

Primary file targets:

- `rendezvous_services/nginx/rendezvous.conf`
- `rendezvous_services/docker-compose.yml`
- `rendezvous_services/README.md`

Done criteria:

- External websocket connection succeeds only via `wss`.
- Plain `ws`/`http` external access is blocked or redirected.
- Deployment docs show the exact TLS env/config steps.

#### PR-2: Edge Protection and Endpoint Exposure Controls

Purpose: reduce abuse/risk on publicly reachable control-plane endpoints.

Scope:

- Add nginx rate limiting for connection bursts and request floods.
- Add connection and request timeout/size constraints.
- Restrict `/health` and any admin/diagnostic routes to trusted sources.
- Add explicit logging fields for rejected/limited requests.

Primary file targets:

- `rendezvous_services/nginx/rendezvous.conf`
- `rendezvous_services/docker-compose.yml`
- `rendezvous_services/server/main.py` (only if auth-gated health mode is added)
- `rendezvous_services/README.md`

Done criteria:

- Excessive connect/request rates are throttled with expected status codes.
- Diagnostics endpoints are no longer publicly open by default.
- Logs include enough detail to distinguish block, throttle, and upstream failures.

#### PR-3: TLS Hardening Profile and Operations Runbook

Purpose: finalize production-grade TLS settings and operational readiness.

Scope:

- Enforce TLS 1.2+ and prefer TLS 1.3 where available.
- Configure strong ciphers/protocol settings and secure headers.
- Add certificate renewal/rotation instructions.
- Add validation checklist for staging and production cutover.

Primary file targets:

- `rendezvous_services/nginx/rendezvous.conf`
- `rendezvous_services/README.md`
- `INSTALLER.md` (if publishing checklist is impacted)

Done criteria:

- TLS scan returns acceptable grade for configured domain.
- Certificate rotation steps are documented and tested.
- A repeatable pre-release security verification checklist is present.
- Relay latency validation gate passes for Option A (`p95` overhead <= 5 ms).

### Phase 2 Kickoff (Auth Foundation PRs)

- [x] Add JWT validation middleware/dependency in rendezvous server.
- [x] Enforce token checks on register/connect/relay request paths.
- [x] Define and validate mandatory claims (`sub`, `role`, scope identifiers, `exp`, `jti`).
   - Implemented in kickoff: `sub`, `iat`, `exp`, `jti`, endpoint role checks (`role`/`roles`, `admin` override), optional endpoint scope checks.
- [ ] Introduce short-lived connection grant token model for host-client session setup.
- [x] Add anti-replay cache keyed by `jti` with bounded TTL.
- [x] Add migration toggle to allow temporary legacy shared-token mode.

### Phase 3 Kickoff (Protocol Security PRs)

- [ ] Publish protocol draft: handshake messages, crypto suites, key schedule, frame format.
- [ ] Add identity key storage abstraction and secure loading path for host/client.
- [ ] Implement handshake transcript signing and verification.
- [ ] Implement AEAD-encrypted frame codec with sequence-based nonce handling.
- [ ] Add explicit downgrade protection in negotiation logic.
- [ ] Ensure relay forwarding path remains payload-opaque (ciphertext only).

### Phase 4 Kickoff (Behavior and Policy PRs)

- [ ] Unify direct and relay transport behavior under encrypted framing.
- [ ] Add secure-default configuration profile.
- [ ] Add guarded debug-only insecure override flags.
- [ ] Add user-facing diagnostics for security policy failures (without leaking secrets).

### Phase 5 Kickoff (Validation and Rollout PRs)

- [ ] Add security telemetry metrics (auth failures, handshake failures, replay rejects, decrypt failures).
- [ ] Add latency telemetry for handshake and keying p50/p95.
- [ ] Add integration tests for secure direct, secure relay, expiry, and replay paths.
- [ ] Add staged rollout flags and environment defaults for progressive enforcement.
- [ ] Add runbooks for cert rotation, key rotation, and security incident response.

### Acceptance Gates

Before moving from one phase to the next, confirm all of the following:

- [ ] Automated tests pass in CI for new security behavior.
- [ ] No regression beyond agreed keying latency budget.
- [ ] Backward compatibility behavior matches migration policy.
- [ ] Operational docs and rollback steps are updated.
- [ ] Health/metrics surfaces provide enough signal for production monitoring.
