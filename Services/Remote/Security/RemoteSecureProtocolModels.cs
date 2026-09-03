using System;
using System.Collections.Generic;

namespace NetKeyer.Services.Remote.Security;

public static class RemoteSecureProtocolDefaults
{
    public const int SecureProtocolVersion = 1;
    public const string HandshakeSuite = "P256+ECDSA+HKDF-SHA256";
    public const string FrameAead = "AES-GCM";
}

public sealed class RemoteHandshakeHello
{
    public int SecureProtocolVersion { get; set; } = RemoteSecureProtocolDefaults.SecureProtocolVersion;
    public string IdentityKeyId { get; set; } = string.Empty;
    public byte[] IdentityPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();
    public List<string> SupportedSuites { get; set; } = new();
}

public sealed class RemoteHandshakeResponse
{
    public int SecureProtocolVersion { get; set; } = RemoteSecureProtocolDefaults.SecureProtocolVersion;
    public string SelectedSuite { get; set; } = RemoteSecureProtocolDefaults.HandshakeSuite;
    public string SessionId { get; set; } = string.Empty;
    public byte[] IdentitySignature { get; set; } = Array.Empty<byte>();
    public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();
}

public sealed class RemoteHandshakeResult
{
    public string SessionId { get; set; } = string.Empty;
    public bool IsDirectPath { get; set; }
    public string SelectedSuite { get; set; } = string.Empty;
    public double HandshakeDurationMs { get; set; }
    public byte[] SendKey { get; set; } = Array.Empty<byte>();
    public byte[] ReceiveKey { get; set; } = Array.Empty<byte>();
    public byte[] SendNoncePrefix { get; set; } = Array.Empty<byte>();
    public byte[] ReceiveNoncePrefix { get; set; } = Array.Empty<byte>();
}

public sealed class RemoteEncryptedFrame
{
    public ulong Sequence { get; set; }
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] AuthTag { get; set; } = Array.Empty<byte>();
}
