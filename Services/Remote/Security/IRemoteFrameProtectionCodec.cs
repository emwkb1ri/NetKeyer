using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public interface IRemoteFrameProtectionCodec
{
    Task<RemoteEncryptedFrame> EncryptAsync(byte[] plaintextFrame, CancellationToken ct);
    Task<byte[]> DecryptAsync(RemoteEncryptedFrame encryptedFrame, CancellationToken ct);
}
