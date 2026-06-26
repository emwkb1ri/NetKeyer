using System;

namespace NetKeyer.Services.Remote;

public enum RemoteClientSessionStatus
{
    Connected,
    Disconnected
}

public class RemoteClientStatusInfo
{
    public string ClientId { get; set; } = "";
    public string RemoteEndpoint { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string HostName { get; set; } = "";
    public RemoteClientSessionStatus Status { get; set; } = RemoteClientSessionStatus.Connected;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}
