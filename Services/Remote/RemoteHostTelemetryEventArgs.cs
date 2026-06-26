using System;

namespace NetKeyer.Services.Remote;

public class RemoteHostTelemetryEventArgs : EventArgs
{
    public double LastLagMs { get; init; }
    public double AvgLagMs { get; init; }
    public double MaxLagMs { get; init; }
    public double JitterMs { get; init; }
    public long AcceptedFrames { get; init; }
    public long AcceptedFramesLast60s { get; init; }
    public long DroppedStaleFrames { get; init; }
}
