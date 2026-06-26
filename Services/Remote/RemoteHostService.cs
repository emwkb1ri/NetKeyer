using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public class RemoteHostService : IRemoteHostService
{
    private static readonly TimeSpan StaleClientEntryAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, RemoteClientSession> _sessions = new();
    private readonly List<RemoteClientStatusInfo> _clientStatuses = new();
    private readonly object _clientStatusesLock = new();

    private TcpListener _listener;
    private CancellationTokenSource _internalCts;
    private Task _acceptLoopTask;
    private RemoteHostOptions _options;

    public bool IsListening => _listener != null;
    public int ConnectedClientCount => _sessions.Count;

    public event EventHandler<string> HostStatusChanged;
    public event EventHandler<int> ConnectedClientCountChanged;
    public event EventHandler<IReadOnlyList<RemoteClientStatusInfo>> ClientStatusesChanged;
    public event EventHandler<RemotePaddleStateEventArgs> PaddleStateReceived;

    public async Task StartAsync(RemoteHostOptions options, CancellationToken ct)
    {
        await StopAsync();

        _options = options ?? new RemoteHostOptions();
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (!IPAddress.TryParse(_options.BindAddress, out var bindIp))
        {
            bindIp = IPAddress.Any;
        }

        _listener = new TcpListener(bindIp, _options.ListenPort);
        _listener.Start();

        RaiseStatus($"Listening on {bindIp}:{_options.ListenPort} (max {_options.MaxClients} clients)");
        DebugLogger.Log("remote", $"Host listening on {bindIp}:{_options.ListenPort}");

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_internalCts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _internalCts;
        var acceptTask = _acceptLoopTask;

        _internalCts = null;
        _acceptLoopTask = null;

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        try { _listener?.Stop(); } catch { }
        _listener = null;

        if (acceptTask != null)
        {
            try { await acceptTask; } catch { }
        }

        foreach (var session in _sessions.Values.ToList())
        {
            try { session.Dispose(); } catch { }
        }

        MarkAllSessionsDisconnected();
        _sessions.Clear();
        RaiseClientCount();
        RaiseStatus("Host stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);

                if (_sessions.Count >= _options.MaxClients)
                {
                    DebugLogger.Log("remote", "Rejecting remote client because max clients reached");
                    try { client.Close(); } catch { }
                    continue;
                }

                var session = new RemoteClientSession(client, _options.SharedToken, _options.HostName);
                session.PaddleStateReceived += Session_PaddleStateReceived;
                session.SessionClosed += Session_SessionClosed;
                session.SessionMetadataChanged += Session_SessionMetadataChanged;

                _sessions[session.ClientId] = session;
                UpsertClientStatus(session, RemoteClientSessionStatus.Connected);
                RaiseClientCount();

                _ = Task.Run(() => session.RunAsync(ct), ct);

                DebugLogger.Log("remote", $"Accepted remote session {session.ClientId} from {session.RemoteEndpoint}");
                RaiseStatus($"Listening on port {_options.ListenPort}. Connected clients: {_sessions.Count}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseStatus($"Host listener error: {ex.Message}");
            DebugLogger.Log("remote", $"Host accept loop failed: {ex.Message}");
        }
    }

    private void Session_PaddleStateReceived(object sender, RemotePaddleStateEventArgs e)
    {
        MarkClientActive(e?.ClientId);
        PaddleStateReceived?.Invoke(this, e);
    }

    private void Session_SessionMetadataChanged(object sender, RemoteClientSession session)
    {
        if (session == null)
        {
            return;
        }

        UpsertClientStatus(session, RemoteClientSessionStatus.Connected);
    }

    private void Session_SessionClosed(object sender, RemoteClientSession e)
    {
        if (e != null)
        {
            _sessions.TryRemove(e.ClientId, out _);
            UpsertClientStatus(e, RemoteClientSessionStatus.Disconnected);
            try { e.Dispose(); } catch { }
        }

        RaiseClientCount();
        RaiseStatus($"Listening on port {_options.ListenPort}. Connected clients: {_sessions.Count}");
    }

    private void RaiseStatus(string status)
    {
        HostStatusChanged?.Invoke(this, status);
    }

    private void RaiseClientCount()
    {
        ConnectedClientCountChanged?.Invoke(this, _sessions.Count);
    }

    private void MarkAllSessionsDisconnected()
    {
        foreach (var session in _sessions.Values)
        {
            UpsertClientStatus(session, RemoteClientSessionStatus.Disconnected);
        }
    }

    private void UpsertClientStatus(RemoteClientSession session, RemoteClientSessionStatus status)
    {
        if (session == null)
        {
            return;
        }

        lock (_clientStatusesLock)
        {
            var existing = _clientStatuses.FirstOrDefault(s => s.ClientId == session.ClientId);
            if (existing == null)
            {
                // Reuse a disconnected row for the same client identity so reconnects update in-place.
                existing = _clientStatuses
                    .Where(s => s.Status == RemoteClientSessionStatus.Disconnected)
                    .FirstOrDefault(s => IsSameClientIdentity(s, session));
            }

            if (existing == null)
            {
                existing = _clientStatuses.FirstOrDefault(s => IsSameClientIdentity(s, session));
            }

            if (existing == null)
            {
                existing = new RemoteClientStatusInfo
                {
                    ClientId = session.ClientId,
                    FirstSeenUtc = DateTime.UtcNow
                };
                _clientStatuses.Add(existing);
            }
            else
            {
                existing.ClientId = session.ClientId;
            }

            existing.RemoteEndpoint = session.RemoteEndpoint;
            existing.RemoteIp = session.RemoteIp;
            existing.Callsign = session.Callsign ?? "";
            existing.HostName = _options?.HostName ?? "";
            existing.Status = status;
            existing.LastUpdatedUtc = DateTime.UtcNow;

            RemoveStaleDisconnectedEntries();
            EnsureHistoryCapacity();
            PublishClientStatuses();
        }
    }

    private void MarkClientActive(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        lock (_clientStatusesLock)
        {
            var existing = _clientStatuses.FirstOrDefault(s => s.ClientId == clientId);
            if (existing == null)
            {
                return;
            }

            if (existing.Status == RemoteClientSessionStatus.Connected)
            {
                existing.LastUpdatedUtc = DateTime.UtcNow;
                RemoveStaleDisconnectedEntries();
                PublishClientStatuses();
            }
        }
    }

    private bool IsSameClientIdentity(RemoteClientStatusInfo existing, RemoteClientSession session)
    {
        if (existing == null || session == null)
        {
            return false;
        }

        string existingIp = existing.RemoteIp?.Trim() ?? string.Empty;
        string sessionIp = session.RemoteIp?.Trim() ?? string.Empty;
        if (!string.Equals(existingIp, sessionIp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string existingCall = existing.Callsign?.Trim() ?? string.Empty;
        string sessionCall = session.Callsign?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(existingCall) || string.IsNullOrWhiteSpace(sessionCall))
        {
            return true;
        }

        return string.Equals(existingCall, sessionCall, StringComparison.OrdinalIgnoreCase);
    }

    private void PublishClientStatuses()
    {
        var snapshot = _clientStatuses
            .OrderByDescending(s => s.Status == RemoteClientSessionStatus.Connected)
            .ThenByDescending(s => s.LastUpdatedUtc)
            .Select(s => new RemoteClientStatusInfo
            {
                ClientId = s.ClientId,
                RemoteEndpoint = s.RemoteEndpoint,
                RemoteIp = s.RemoteIp,
                Callsign = s.Callsign,
                HostName = s.HostName,
                Status = s.Status,
                FirstSeenUtc = s.FirstSeenUtc,
                LastUpdatedUtc = s.LastUpdatedUtc
            })
            .ToList();

        ClientStatusesChanged?.Invoke(this, snapshot);
    }

    private void EnsureHistoryCapacity()
    {
        int max = Math.Max(1, _options?.MaxClients ?? 5);

        while (_clientStatuses.Count > max)
        {
            var disconnected = _clientStatuses
                .Where(s => s.Status == RemoteClientSessionStatus.Disconnected)
                .OrderBy(s => s.LastUpdatedUtc)
                .FirstOrDefault();

            if (disconnected != null)
            {
                _clientStatuses.Remove(disconnected);
                continue;
            }

            // All entries are connected; keep the newest sessions represented.
            var oldest = _clientStatuses.OrderBy(s => s.LastUpdatedUtc).FirstOrDefault();
            if (oldest != null)
            {
                _clientStatuses.Remove(oldest);
            }
        }
    }

    private void RemoveStaleDisconnectedEntries()
    {
        DateTime cutoff = DateTime.UtcNow - StaleClientEntryAge;
        _clientStatuses.RemoveAll(s => s.Status == RemoteClientSessionStatus.Disconnected && s.LastUpdatedUtc < cutoff);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
