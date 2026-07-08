using System;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;
using Open.Nat;

namespace NetKeyer.Services.Remote;

public sealed class HostPortMapper : IHostPortMapper
{
    private const int MappingLifetimeSeconds = 3600;

    private readonly NatDiscoverer _discoverer = new NatDiscoverer();
    private NatDevice _device;
    private Mapping _activeMapping;

    public async Task<HostPortMapResult> TryMapTcpPortAsync(int internalPort, CancellationToken ct)
    {
        if (internalPort <= 0 || internalPort > 65535)
        {
            return new HostPortMapResult();
        }

        NatDevice device = await DiscoverDeviceAsync(ct);
        if (device == null)
        {
            return new HostPortMapResult();
        }

        var mapping = new Mapping(Protocol.Tcp, internalPort, internalPort, MappingLifetimeSeconds, "NetKeyer TCP host");

        try
        {
            await device.CreatePortMapAsync(mapping);
            _device = device;
            _activeMapping = mapping;

            string externalIp = string.Empty;
            try
            {
                var ip = await device.GetExternalIPAsync();
                externalIp = ip?.ToString() ?? string.Empty;
            }
            catch
            {
                // Router may not report external IP; mapped port can still be usable.
            }

            DebugLogger.LogAlways("rendezvous", $"Automatic TCP port mapping succeeded: internal={internalPort}, external={mapping.PublicPort}, externalIp={externalIp}");

            return new HostPortMapResult
            {
                Success = true,
                PublicIp = externalIp,
                PublicPort = mapping.PublicPort
            };
        }
        catch (Exception ex)
        {
            DebugLogger.LogAlways("rendezvous", $"Automatic TCP port mapping failed for {internalPort}: {ex.Message}");
            return new HostPortMapResult();
        }
    }

    private async Task<NatDevice> DiscoverDeviceAsync(CancellationToken ct)
    {
        try
        {
            return await DiscoverWithMapperAsync(PortMapper.Upnp, ct);
        }
        catch
        {
        }

        try
        {
            return await DiscoverWithMapperAsync(PortMapper.Pmp, ct);
        }
        catch
        {
        }

        return null;
    }

    private async Task<NatDevice> DiscoverWithMapperAsync(PortMapper mapper, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(3));
        return await _discoverer.DiscoverDeviceAsync(mapper, linked);
    }

    public void Dispose()
    {
        if (_device != null && _activeMapping != null)
        {
            try
            {
                _device.DeletePortMapAsync(_activeMapping).GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        _activeMapping = null;
        _device = null;
    }
}
