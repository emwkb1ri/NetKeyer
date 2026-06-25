using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote;

public interface IRemoteHostService : IDisposable
{
    bool IsListening { get; }
    int ConnectedClientCount { get; }

    event EventHandler<string> HostStatusChanged;
    event EventHandler<int> ConnectedClientCountChanged;
    event EventHandler<RemotePaddleStateEventArgs> PaddleStateReceived;

    Task StartAsync(RemoteHostOptions options, CancellationToken ct);
    Task StopAsync();
}
