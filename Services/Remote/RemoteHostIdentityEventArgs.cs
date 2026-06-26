using System;

namespace NetKeyer.Services.Remote;

public class RemoteHostIdentityEventArgs : EventArgs
{
    public string HostIp { get; init; } = "";
    public string HostName { get; init; } = "";
}
