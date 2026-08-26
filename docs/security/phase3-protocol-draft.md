# NetKeyer Phase 3 Protocol Draft (Initial)

Status: Draft 0 (scaffolding)
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

- Identity keys: Ed25519
- Ephemeral key exchange: X25519
- Key derivation: HKDF-SHA256
- AEAD framing target: ChaCha20-Poly1305 (default), AES-GCM optional

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
