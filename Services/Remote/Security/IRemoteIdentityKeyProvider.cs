using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public interface IRemoteIdentityKeyProvider
{
    Task<RemoteIdentityKeyPair> GetOrCreateIdentityAsync(CancellationToken ct);
    Task<RemotePublicIdentity> GetPublicIdentityAsync(CancellationToken ct);
}
