using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

// Phase 3 scaffolding: placeholder codec to keep transport integration low-risk.
public sealed class NullRemoteFrameProtectionCodec : IRemoteFrameProtectionCodec
{
    public Task<RemoteEncryptedFrame> EncryptAsync(byte[] plaintextFrame, CancellationToken ct)
    {
        if (plaintextFrame == null)
        {
            throw new ArgumentNullException(nameof(plaintextFrame));
        }

        var frame = new RemoteEncryptedFrame
        {
            Sequence = 0,
            Nonce = Array.Empty<byte>(),
            Ciphertext = plaintextFrame,
            AuthTag = Array.Empty<byte>(),
        };

        return Task.FromResult(frame);
    }

    public Task<byte[]> DecryptAsync(RemoteEncryptedFrame encryptedFrame, CancellationToken ct)
    {
        if (encryptedFrame == null)
        {
            throw new ArgumentNullException(nameof(encryptedFrame));
        }

        return Task.FromResult(encryptedFrame.Ciphertext ?? Array.Empty<byte>());
    }
}
