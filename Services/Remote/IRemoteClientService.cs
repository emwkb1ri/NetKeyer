using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote;

public interface IRemoteClientService : IDisposable
{
    bool IsConnected { get; }
    event EventHandler<string> ConnectionStatusChanged;
    event EventHandler<RemoteHostIdentityEventArgs> HostIdentityChanged;
    event EventHandler<RemoteHostTelemetryEventArgs> HostTelemetryChanged;

    Task ConnectAsync(RemoteClientOptions options, CancellationToken ct);
    Task DisconnectAsync();
    ValueTask SendPaddleStateAsync(PaddleStatePayload payload, CancellationToken ct);
}
