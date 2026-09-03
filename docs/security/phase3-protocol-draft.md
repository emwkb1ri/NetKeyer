# NetKeyer Phase 3 Protocol Draft (Initial)

Status: Draft 1 (initial implementation)
Date: 2026-08-26

## Scope

This draft defines the initial secure transport protocol shape for NetKeyer remote mode.
It is intended to secure both direct and relay paths with the same handshake and frame protection behavior.

## Security Goals

- Encrypt all paddle/control traffic end-to-end.
- Authenticate peer identities cryptographically.
- Prevent replay and downgrade attacks.
- Keep relay payload opaque (ciphertext only).

## Handshake Overview

1. Client sends `RemoteHandshakeHello`:
- `secureProtocolVersion`
- `identityKeyId`
- `identityPublicKey` (Ed25519)
- `ephemeralPublicKey` (X25519)
- `supportedSuites`

2. Host replies with `RemoteHandshakeResponse`:
- `secureProtocolVersion`
- `selectedSuite`
- `sessionId`
- `identitySignature` (signature over transcript)
- `ephemeralPublicKey`

3. Both sides derive directional traffic keys:
- KDF: HKDF-SHA256
- Inputs: ECDH shared secret + transcript hash + rendezvous/session context

4. Both sides switch to encrypted frame mode.

## Cryptographic Baseline

- Identity keys (current implementation): ECDSA P-256
- Ephemeral key exchange (current implementation): ECDH P-256
- Key derivation: HKDF-SHA256
- AEAD framing (current implementation): AES-GCM
- Target upgrade path: Ed25519 + X25519 + ChaCha20-Poly1305

## Frame Format (Planned)

Each encrypted frame uses:
- `sequence` (monotonic, uint64)
- `nonce` (derived from sequence and direction)
- `ciphertext`
- `authTag`

Associated data should include at minimum:
- protocol version
- message type
- sequence
- session id

## Anti-Replay / Anti-Downgrade

- Reject duplicate or out-of-window sequence numbers.
- Refuse downgraded suites/versions when stronger configured policy is available.
- Bind session keys to negotiated version and suite.

## Rendezvous Binding

Phase 3 handshake should bind to Phase 2 grant context:
- host id
- client id
- grant session id
- grant token id (`jti`)

## Incremental Plan

1. Finalize transcript structure and exact signature payload.
2. Implement `IRemoteSecureSessionNegotiator` over existing transport streams.
3. Add concrete `IRemoteFrameProtectionCodec` implementation.
4. Add integration tests for secure direct and secure relay paths.

## Current Scaffolding in Code

- `Services/Remote/Security/IRemoteIdentityKeyProvider.cs`
- `Services/Remote/Security/IRemoteSecureSessionNegotiator.cs`
- `Services/Remote/Security/IRemoteFrameProtectionCodec.cs`
- `Services/Remote/Security/RemoteSecureProtocolModels.cs`
- `Services/Remote/Security/NullRemoteFrameProtectionCodec.cs`

## Initial Implemented Path (Feature-Flagged)

- Secure handshake messages are now exchanged in-band on the remote stream:
	- `secureHandshakeHello`
	- `secureHandshakeResponse`
- Transcript hash/signature verification is performed during handshake.
- Directional traffic keys and nonce prefixes are derived from handshake shared secret.
- Post-handshake control frames are wrapped in `secureFrame` payloads and AEAD protected.
- Heartbeat telemetry now carries secure handshake duration plus normalized keying lag summaries (`last`, `p50`, `p95`, `max`) for remote latency observability.

Feature flags:

- `NETKEYER_DEBUG_ALLOW_INSECURE_OVERRIDES`
	- Debug gate. Must be `true` before insecure opt-out values are honored, and ignored in non-debug builds.
- `NETKEYER_ENABLE_SECURE_REMOTE_TRANSPORT`
	- Secure default: enabled when unset; set to `0|false|no|off` to disable for local/lab compatibility testing only when debug gate is enabled.
- `NETKEYER_REQUIRE_SECURE_REMOTE_TRANSPORT`
	- Secure default: enabled when unset; set to `0|false|no|off` to allow plaintext fallback if handshake fails only when debug gate is enabled.
- `NETKEYER_VALIDATE_RELAY_CIPHERTEXT`
	- Secure default: enabled when unset; set to `0|false|no|off` to allow plaintext envelopes post-handshake only when debug gate is enabled.

## Explicit Downgrade Protection (Implemented)

- Handshake hello/response versions must exactly match the current secure protocol version.
- Negotiated suite must exactly match the currently supported suite.
- Version/suite mismatches are rejected as downgrade/unsupported attempts.
- Empty session IDs in handshake responses are rejected.

## Ciphertext Validation (Implemented)

- With validation enabled, secure handshake must succeed.
- If handshake falls back to plaintext while validation is enabled, the session is rejected.
- After secure mode is active, receiving any non-secure frame causes immediate connection failure on both direct and relay transports.
