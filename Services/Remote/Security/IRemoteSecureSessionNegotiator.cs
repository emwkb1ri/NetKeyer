using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public interface IRemoteSecureSessionNegotiator
{
    Task<RemoteHandshakeResult> NegotiateClientAsync(Stream transport, string expectedHostId, CancellationToken ct);
    Task<RemoteHandshakeResult> NegotiateHostAsync(Stream transport, string expectedClientId, CancellationToken ct);
}
