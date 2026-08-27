using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Rendezvous;

public interface IRendezvousControlService : IDisposable
{
    Task<RendezvousHostRegistrationSession> RegisterHostAsync(RendezvousHostRegistrationOptions options, CancellationToken ct);
    Task<RendezvousClientConnectionSession> ConnectClientAsync(RendezvousClientConnectOptions options, CancellationToken ct);
    Task<IReadOnlyList<RendezvousHostSummary>> ListHostsAsync(RendezvousHostListRequestOptions options, CancellationToken ct);
    Task<bool> WaitForMappedEndpointAsync(RendezvousClientConnectionSession session, TimeSpan timeout, CancellationToken ct);
    Task<bool> WaitForRelayAsync(RendezvousClientConnectionSession session, TimeSpan timeout, CancellationToken ct);
    void StartClientControlMonitor(RendezvousClientConnectionSession session);
}
