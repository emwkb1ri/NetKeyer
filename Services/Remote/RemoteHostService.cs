using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public class RemoteHostService : IRemoteHostService
{
    private static readonly TimeSpan StaleClientEntryAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan TelemetryMaxLagWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, RemoteClientSession> _sessions = new();
    private readonly ConcurrentDictionary<string, TelemetryState> _telemetryByClientId = new();
    private readonly List<RemoteClientStatusInfo> _clientStatuses = new();
    private readonly object _clientStatusesLock = new();
    private readonly object _ownershipLock = new();

    private TcpListener _listener;
    private CancellationTokenSource _internalCts;
    private Task _acceptLoopTask;
    private RemoteHostOptions _options;
    private string _activeOwnerClientId;
    private DateTime _activeOwnerLeaseUntilUtc = DateTime.MinValue;
    private bool _useSenderTickStaleGate;

    private sealed class TelemetryState
    {
        public readonly Queue<(DateTime TimestampUtc, double LagMs)> LagSamples = new();
        public readonly Queue<DateTime> AcceptedFrameTimestamps = new();
        public long MinRawApparentAgeMs = long.MaxValue;
        public long MinRawSenderTickAgeMs = long.MaxValue;
        public double LastRawApparentAgeMs;
        public double LastLagMs;
        public double AvgLagMs;
        public double MaxLagMs;
        public double JitterMs;
        public long AcceptedFrames;
        public long AcceptedFramesLast60s;
        public long DroppedStaleFrames;
        public DateTime NextLogAtUtc = DateTime.UtcNow;
    }

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
        _useSenderTickStaleGate = _options.UseSenderTickStaleGate;
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ResetActiveOwner();

        if (!IPAddress.TryParse(_options.BindAddress, out var bindIp))
        {
            bindIp = IPAddress.Any;
        }

        _listener = new TcpListener(bindIp, _options.ListenPort);
        _listener.Start();

        RaiseStatus($"Listening on {bindIp}:{_options.ListenPort} (max {_options.MaxClients} clients)");
        DebugLogger.Log("remote", $"Host listening on {bindIp}:{_options.ListenPort}");
        DebugLogger.LogAlways("remote", $"Stale frame gate mode: {(_useSenderTickStaleGate ? "sender-tick" : "normalized-lag")}");

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_internalCts.Token));
    }

    public async Task ConnectRelaySessionAsync(string relayHost, int relayPort, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relayHost))
        {
            throw new ArgumentException("Relay host is required.", nameof(relayHost));
        }

        if (relayPort <= 0)
        {
            throw new ArgumentException("Relay port must be greater than 0.", nameof(relayPort));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Relay session ID is required.", nameof(sessionId));
        }

        if (_options == null)
        {
            throw new InvalidOperationException("Host service must be started before opening relay sessions.");
        }

        if (_sessions.Count >= _options.MaxClients)
        {
            throw new InvalidOperationException("Cannot open relay session because max clients has been reached.");
        }

        var relayClient = new TcpClient();
        await relayClient.ConnectAsync(relayHost, relayPort, ct);
        var stream = relayClient.GetStream();

        string handshake = $"SESSION {sessionId.Trim()} HOST\n";
        byte[] bytes = Encoding.UTF8.GetBytes(handshake);
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
        await stream.FlushAsync(ct);

        AddAndRunSession(relayClient, _internalCts?.Token ?? ct, "relay");
        DebugLogger.LogAlways("remote", $"Host relay session connected (transport=relay): relay={relayHost}:{relayPort} session={sessionId}");
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
        _telemetryByClientId.Clear();
        ResetActiveOwner();
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

                AddAndRunSession(client, ct, "direct");
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
        if (e == null || e.State == null || string.IsNullOrWhiteSpace(e.ClientId))
        {
            return;
        }

        long rawApparentAgeMs = e.ApparentAgeMs;
        long rawSenderTickAgeMs = e.SenderTickAgeMs;
        int staleThresholdMs = Math.Max(1, _options?.StaleFrameDropMs ?? RemoteDefaults.DefaultStaleFrameDropMs);

        double staleLagMs = GetNormalizedStaleLagForDecision(e.ClientId, rawApparentAgeMs, rawSenderTickAgeMs);
        if (staleLagMs > staleThresholdMs)
        {
            UpdateTelemetry(e.ClientId, rawApparentAgeMs, accepted: false);
            string gateSource = _useSenderTickStaleGate ? "sender-tick" : "normalized-lag";
            DebugLogger.Log("remote", $"Dropping stale paddle frame from {e.ClientId}: lag={staleLagMs:F1}ms threshold={staleThresholdMs}ms mode={gateSource} raw={rawApparentAgeMs}ms tick_raw={rawSenderTickAgeMs}ms seq={e.Sequence}");
            return;
        }

        if (!TryAcquireOrRefreshOwnership(e.ClientId))
        {
            DebugLogger.Log("remote", $"Ignoring paddle state from non-owner client {e.ClientId}; owner={_activeOwnerClientId}");
            return;
        }

        UpdateTelemetry(e.ClientId, rawApparentAgeMs, accepted: true);

        MarkClientActive(e?.ClientId);
        PaddleStateReceived?.Invoke(this, e);
    }

    private double GetNormalizedStaleLagForDecision(string clientId, long rawApparentAgeMs, long rawSenderTickAgeMs)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return 0;
        }

        var telemetry = _telemetryByClientId.GetOrAdd(clientId, _ => new TelemetryState());
        lock (telemetry)
        {
            if (_useSenderTickStaleGate)
            {
                if (rawSenderTickAgeMs < telemetry.MinRawSenderTickAgeMs)
                {
                    telemetry.MinRawSenderTickAgeMs = rawSenderTickAgeMs;
                }

                double normalizedTickLag = rawSenderTickAgeMs - telemetry.MinRawSenderTickAgeMs;
                return normalizedTickLag < 0 ? 0 : normalizedTickLag;
            }

            long baseline = telemetry.MinRawApparentAgeMs;
            if (rawApparentAgeMs < baseline)
            {
                baseline = rawApparentAgeMs;
            }

            double normalizedLag = rawApparentAgeMs - baseline;
            return normalizedLag < 0 ? 0 : normalizedLag;
        }
    }

    private void AddAndRunSession(TcpClient client, CancellationToken ct, string transport)
    {
        var session = new RemoteClientSession(client, _options.SharedToken, _options.HostName, BuildHeartbeatPayloadForClient);
        session.PaddleStateReceived += Session_PaddleStateReceived;
        session.SessionClosed += Session_SessionClosed;
        session.SessionMetadataChanged += Session_SessionMetadataChanged;

        _sessions[session.ClientId] = session;
        UpsertClientStatus(session, RemoteClientSessionStatus.Connected);
        RaiseClientCount();

        _ = Task.Run(() => session.RunAsync(ct), ct);

        string label = string.IsNullOrWhiteSpace(transport) ? "unknown" : transport;
        DebugLogger.LogAlways("remote", $"Accepted remote session {session.ClientId} from {session.RemoteEndpoint} (transport={label})");
        RaiseStatus($"Listening on port {_options.ListenPort}. Connected clients: {_sessions.Count}");
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
            ReleaseOwnershipIfOwner(e.ClientId);
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

            if (_telemetryByClientId.TryGetValue(existing.ClientId, out var telemetry))
            {
                existing.LastLagMs = telemetry.LastLagMs;
                existing.AvgLagMs = telemetry.AvgLagMs;
                existing.MaxLagMs = telemetry.MaxLagMs;
                existing.JitterMs = telemetry.JitterMs;
                existing.AcceptedFrames = telemetry.AcceptedFrames;
                existing.AcceptedFramesLast60s = telemetry.AcceptedFramesLast60s;
                existing.DroppedStaleFrames = telemetry.DroppedStaleFrames;
            }

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
                LastUpdatedUtc = s.LastUpdatedUtc,
                LastLagMs = s.LastLagMs,
                AvgLagMs = s.AvgLagMs,
                MaxLagMs = s.MaxLagMs,
                JitterMs = s.JitterMs,
                AcceptedFrames = s.AcceptedFrames,
                AcceptedFramesLast60s = s.AcceptedFramesLast60s,
                DroppedStaleFrames = s.DroppedStaleFrames
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
                _telemetryByClientId.TryRemove(disconnected.ClientId, out _);
                _clientStatuses.Remove(disconnected);
                continue;
            }

            // All entries are connected; keep the newest sessions represented.
            var oldest = _clientStatuses.OrderBy(s => s.LastUpdatedUtc).FirstOrDefault();
            if (oldest != null)
            {
                _telemetryByClientId.TryRemove(oldest.ClientId, out _);
                _clientStatuses.Remove(oldest);
            }
        }
    }

    private void RemoveStaleDisconnectedEntries()
    {
        DateTime cutoff = DateTime.UtcNow - StaleClientEntryAge;
        var stale = _clientStatuses
            .Where(s => s.Status == RemoteClientSessionStatus.Disconnected && s.LastUpdatedUtc < cutoff)
            .ToList();

        foreach (var row in stale)
        {
            _telemetryByClientId.TryRemove(row.ClientId, out _);
            _clientStatuses.Remove(row);
        }
    }

    private bool TryAcquireOrRefreshOwnership(string clientId)
    {
        int holdMs = Math.Max(RemoteDefaults.MinClientHoldMs,
            Math.Min(RemoteDefaults.MaxClientHoldMs, _options?.ActiveClientHoldMs ?? RemoteDefaults.DefaultClientHoldMs));
        DateTime now = DateTime.UtcNow;

        lock (_ownershipLock)
        {
            bool ownerExpired = now >= _activeOwnerLeaseUntilUtc;
            bool noOwner = string.IsNullOrWhiteSpace(_activeOwnerClientId);
            bool isOwner = string.Equals(_activeOwnerClientId, clientId, StringComparison.Ordinal);

            if (noOwner || ownerExpired || isOwner)
            {
                _activeOwnerClientId = clientId;
                _activeOwnerLeaseUntilUtc = now.AddMilliseconds(holdMs);
                return true;
            }
        }

        return false;
    }

    private void ReleaseOwnershipIfOwner(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        lock (_ownershipLock)
        {
            if (!string.Equals(_activeOwnerClientId, clientId, StringComparison.Ordinal))
            {
                return;
            }

            _activeOwnerClientId = null;
            _activeOwnerLeaseUntilUtc = DateTime.MinValue;
        }
    }

    private void ResetActiveOwner()
    {
        lock (_ownershipLock)
        {
            _activeOwnerClientId = null;
            _activeOwnerLeaseUntilUtc = DateTime.MinValue;
        }
    }

    private void UpdateTelemetry(string clientId, long rawApparentAgeMs, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var telemetry = _telemetryByClientId.GetOrAdd(clientId, _ => new TelemetryState());

        lock (telemetry)
        {
            telemetry.LastRawApparentAgeMs = rawApparentAgeMs;
            if (rawApparentAgeMs < telemetry.MinRawApparentAgeMs)
            {
                telemetry.MinRawApparentAgeMs = rawApparentAgeMs;
            }

            // Normalize against the best (minimum) observed apparent age for this client
            // to remove constant clock skew and baseline path delay from displayed lag.
            double lag = rawApparentAgeMs - telemetry.MinRawApparentAgeMs;
            if (lag < 0)
            {
                lag = 0;
            }
            double previousLag = telemetry.LastLagMs;

            // EWMA for stable lag/jitter telemetry without large memory usage.
            telemetry.AvgLagMs = telemetry.AcceptedFrames == 0 && telemetry.DroppedStaleFrames == 0
                ? lag
                : ((telemetry.AvgLagMs * 0.85) + (lag * 0.15));
            telemetry.JitterMs = (telemetry.JitterMs * 0.85) + (Math.Abs(lag - previousLag) * 0.15);
            telemetry.LastLagMs = lag;

            DateTime now = DateTime.UtcNow;
            telemetry.LagSamples.Enqueue((now, lag));
            PruneTelemetryWindows(telemetry, now);
            telemetry.MaxLagMs = telemetry.LagSamples.Count == 0 ? lag : telemetry.LagSamples.Max(s => s.LagMs);

            if (accepted)
            {
                telemetry.AcceptedFrames++;
                telemetry.AcceptedFrameTimestamps.Enqueue(now);
            }
            else
            {
                telemetry.DroppedStaleFrames++;
            }

            telemetry.AcceptedFramesLast60s = telemetry.AcceptedFrameTimestamps.Count;

            MaybeLogTelemetry(clientId, telemetry);
        }

        lock (_clientStatusesLock)
        {
            var existing = _clientStatuses.FirstOrDefault(s => s.ClientId == clientId);
            if (existing == null)
            {
                return;
            }

            existing.LastLagMs = telemetry.LastLagMs;
            existing.AvgLagMs = telemetry.AvgLagMs;
            existing.MaxLagMs = telemetry.MaxLagMs;
            existing.JitterMs = telemetry.JitterMs;
            existing.AcceptedFrames = telemetry.AcceptedFrames;
            existing.AcceptedFramesLast60s = telemetry.AcceptedFramesLast60s;
            existing.DroppedStaleFrames = telemetry.DroppedStaleFrames;
        }
    }

    private static void MaybeLogTelemetry(string clientId, TelemetryState telemetry)
    {
        DateTime now = DateTime.UtcNow;
        if (now < telemetry.NextLogAtUtc)
        {
            return;
        }

        telemetry.NextLogAtUtc = now.AddSeconds(5);
        DebugLogger.LogAlways("remote-telemetry",
            $"Telemetry {clientId}: raw={telemetry.LastRawApparentAgeMs:F1}ms baseline={telemetry.MinRawApparentAgeMs}ms last_norm={telemetry.LastLagMs:F1}ms avg_norm={telemetry.AvgLagMs:F1}ms max_norm_60s={telemetry.MaxLagMs:F1}ms jitter={telemetry.JitterMs:F1}ms accepted60s={telemetry.AcceptedFramesLast60s} accepted={telemetry.AcceptedFrames} dropped_stale={telemetry.DroppedStaleFrames}");
    }

    private HeartbeatPayload BuildHeartbeatPayloadForClient(string clientId)
    {
        var payload = new HeartbeatPayload
        {
            SenderTickMs = Environment.TickCount64
        };

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return payload;
        }

        if (!_telemetryByClientId.TryGetValue(clientId, out var telemetry))
        {
            return payload;
        }

        bool windowTelemetryChanged = false;
        lock (telemetry)
        {
            long previousAcceptedFramesLast60s = telemetry.AcceptedFramesLast60s;
            double previousMaxLagMs = telemetry.MaxLagMs;

            DateTime now = DateTime.UtcNow;
            PruneTelemetryWindows(telemetry, now);
            telemetry.AcceptedFramesLast60s = telemetry.AcceptedFrameTimestamps.Count;
            telemetry.MaxLagMs = telemetry.LagSamples.Count == 0 ? 0 : telemetry.LagSamples.Max(s => s.LagMs);

            windowTelemetryChanged = telemetry.AcceptedFramesLast60s != previousAcceptedFramesLast60s
                || Math.Abs(telemetry.MaxLagMs - previousMaxLagMs) > 0.05;

            payload.LastLagMs = telemetry.LastLagMs;
            payload.AvgLagMs = telemetry.AvgLagMs;
            payload.MaxLagMs = telemetry.MaxLagMs;
            payload.JitterMs = telemetry.JitterMs;
            payload.AcceptedFrames = telemetry.AcceptedFrames;
            payload.AcceptedFramesLast60s = telemetry.AcceptedFramesLast60s;
            payload.DroppedStaleFrames = telemetry.DroppedStaleFrames;
        }

        if (windowTelemetryChanged)
        {
            lock (_clientStatusesLock)
            {
                var existing = _clientStatuses.FirstOrDefault(s => s.ClientId == clientId);
                if (existing != null)
                {
                    existing.MaxLagMs = payload.MaxLagMs;
                    existing.AcceptedFramesLast60s = payload.AcceptedFramesLast60s;
                    PublishClientStatuses();
                }
            }
        }

        return payload;
    }

    private static void PruneTelemetryWindows(TelemetryState telemetry, DateTime nowUtc)
    {
        DateTime cutoff = nowUtc - TelemetryMaxLagWindow;
        while (telemetry.LagSamples.Count > 0 && telemetry.LagSamples.Peek().TimestampUtc < cutoff)
        {
            telemetry.LagSamples.Dequeue();
        }

        while (telemetry.AcceptedFrameTimestamps.Count > 0 && telemetry.AcceptedFrameTimestamps.Peek() < cutoff)
        {
            telemetry.AcceptedFrameTimestamps.Dequeue();
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
