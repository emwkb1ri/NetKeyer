using System;

namespace NetKeyer.Services.Remote;

public class RemotePaddleStateEventArgs : EventArgs
{
    public string ClientId { get; init; }
    public string RemoteEndpoint { get; init; }
    public PaddleStatePayload State { get; init; }
    public long Sequence { get; init; }
    public long SenderTickMs { get; init; }
    public long ReceivedAtTickMs { get; init; }
    public long SenderTickAgeMs { get; init; }
    public long SentAtUnixMs { get; init; }
    public long ReceivedAtUnixMs { get; init; }
    public long ApparentAgeMs { get; init; }
}
