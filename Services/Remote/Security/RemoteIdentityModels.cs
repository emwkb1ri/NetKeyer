using System;

namespace NetKeyer.Services.Remote.Security;

public class RemotePublicIdentity
{
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "Ed25519";
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
}

public sealed class RemoteIdentityKeyPair : RemotePublicIdentity
{
    public byte[] PrivateKey { get; set; } = Array.Empty<byte>();
}
