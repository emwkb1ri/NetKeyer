using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote;

public sealed class HostPortMapResult
{
    public bool Success { get; set; }
    public string PublicIp { get; set; } = "";
    public int PublicPort { get; set; }
}

public interface IHostPortMapper : IDisposable
{
    Task<HostPortMapResult> TryMapTcpPortAsync(int internalPort, CancellationToken ct);
}
