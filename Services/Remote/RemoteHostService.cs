using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public class RemoteHostService : IRemoteHostService
{
    private readonly ConcurrentDictionary<string, RemoteClientSession> _sessions = new();

    private TcpListener _listener;
    private CancellationTokenSource _internalCts;
    private Task _acceptLoopTask;
    private RemoteHostOptions _options;

    public bool IsListening => _listener != null;
    public int ConnectedClientCount => _sessions.Count;

    public event EventHandler<string> HostStatusChanged;
    public event EventHandler<int> ConnectedClientCountChanged;
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

                var session = new RemoteClientSession(client, _options.SharedToken);
                session.PaddleStateReceived += Session_PaddleStateReceived;
                session.SessionClosed += Session_SessionClosed;

                _sessions[session.ClientId] = session;
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
        PaddleStateReceived?.Invoke(this, e);
    }

    private void Session_SessionClosed(object sender, RemoteClientSession e)
    {
        if (e != null)
        {
            _sessions.TryRemove(e.ClientId, out _);
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

    public void Dispose()
    {
        _ = StopAsync();
    }
}
