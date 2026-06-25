using System;

namespace NetKeyer.Services.Remote;

public class RemotePaddleStateEventArgs : EventArgs
{
    public string ClientId { get; init; }
    public string RemoteEndpoint { get; init; }
    public PaddleStatePayload State { get; init; }
    public long ReceivedAtUnixMs { get; init; }
}
