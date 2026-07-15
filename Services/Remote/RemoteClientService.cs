using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public class RemoteClientService : IRemoteClientService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpClient _client;
    private NetworkStream _stream;
    private CancellationTokenSource _internalCts;
    private Task _receiveLoopTask;
    private Task _heartbeatTask;
    private long _sequence;
    private string _connectedHostIp = "";
    private string _connectedHostName = "";
    private string _lastHostErrorMessage = "";
    private DateTime _lastHostErrorUtc = DateTime.MinValue;

    private static readonly TimeSpan HostErrorStatusGuardWindow = TimeSpan.FromSeconds(2);

    public bool IsConnected => _client?.Connected == true && _stream != null;

    public event EventHandler<string> ConnectionStatusChanged;
    public event EventHandler<RemoteHostIdentityEventArgs> HostIdentityChanged;
    public event EventHandler<RemoteHostTelemetryEventArgs> HostTelemetryChanged;

    public async Task ConnectAsync(RemoteClientOptions options, CancellationToken ct)
    {
        await DisconnectAsync();

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var client = new TcpClient();
        await client.ConnectAsync(options.TargetHost, options.TargetPort, _internalCts.Token);

        var stream = client.GetStream();

        if (!string.IsNullOrWhiteSpace(options.RelaySessionId) && !string.IsNullOrWhiteSpace(options.RelayRole))
        {
            string relayRole = options.RelayRole.Trim().ToUpperInvariant();
            string handshake = $"SESSION {options.RelaySessionId.Trim()} {relayRole}\n";
            byte[] bytes = Encoding.UTF8.GetBytes(handshake);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), _internalCts.Token);
            await stream.FlushAsync(_internalCts.Token);
            DebugLogger.Log("remote", $"Relay handshake sent: session={options.RelaySessionId.Trim()} role={relayRole}");
        }

        lock (_sync)
        {
            _client = client;
            _stream = stream;
            _sequence = 0;
        }

        _connectedHostIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? options.TargetHost;
        _connectedHostName = string.Empty;
        ClearRecentHostError();

        await SendControlMessageAsync(RemoteMessageType.Hello, new HelloPayload
        {
            Callsign = options.Callsign ?? ""
        }, _internalCts.Token);

        if (!string.IsNullOrWhiteSpace(options.SharedToken))
        {
            await SendControlMessageAsync(RemoteMessageType.Auth, new AuthPayload { Token = options.SharedToken }, _internalCts.Token);
        }

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_internalCts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_internalCts.Token));

        RaiseStatus($"Connected to {options.TargetHost}:{options.TargetPort}");
        RaiseHostIdentity(_connectedHostIp, _connectedHostName);
        DebugLogger.LogAlways("remote", $"Client connected to {options.TargetHost}:{options.TargetPort}");
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource localCts;
        Task receiveTask;
        Task heartbeatTask;

        lock (_sync)
        {
            localCts = _internalCts;
            receiveTask = _receiveLoopTask;
            heartbeatTask = _heartbeatTask;
            _internalCts = null;
            _receiveLoopTask = null;
            _heartbeatTask = null;
        }

        if (localCts != null)
        {
            try { localCts.Cancel(); } catch { }
            localCts.Dispose();
        }

        if (receiveTask != null)
        {
            try { await receiveTask; } catch { }
        }

        if (heartbeatTask != null)
        {
            try { await heartbeatTask; } catch { }
        }

        lock (_sync)
        {
            try { _stream?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            _stream = null;
            _client = null;
        }

        _connectedHostIp = "";
        _connectedHostName = "";
        ClearRecentHostError();

        RaiseStatus("Disconnected");
        RaiseHostIdentity("", "");
    }

    public async ValueTask SendPaddleStateAsync(PaddleStatePayload payload, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendControlMessageAsync(RemoteMessageType.PaddleState, payload, ct);
    }

    private async Task SendControlMessageAsync<TPayload>(RemoteMessageType type, TPayload payload, CancellationToken ct)
    {
        NetworkStream stream;

        lock (_sync)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            long seq = Interlocked.Increment(ref _sequence);
            var envelope = RemoteProtocolJson.CreateEnvelope(type, seq, payload);
            await RemoteFrameCodec.WriteEnvelopeAsync(stream, envelope, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NetworkStream stream;
                lock (_sync)
                {
                    stream = _stream;
                }

                if (stream == null)
                {
                    return;
                }

                var envelope = await RemoteFrameCodec.ReadEnvelopeAsync(stream, ct);
                if (envelope.Type == RemoteMessageType.Error)
                {
                    var payload = RemoteProtocolJson.DeserializePayload<ErrorPayload>(envelope);
                    string hostErrorMessage = payload?.Message ?? "Unknown";
                    RememberHostError(hostErrorMessage);
                    RaiseStatus($"Host error: {hostErrorMessage}");
                    DebugLogger.LogAlways("remote", $"Host error payload: {hostErrorMessage}");
                }
                else if (envelope.Type == RemoteMessageType.Hello)
                {
                    var hello = RemoteProtocolJson.DeserializePayload<HelloPayload>(envelope);
                    _connectedHostName = hello?.HostName ?? "";
                    RaiseHostIdentity(_connectedHostIp, _connectedHostName);
                    DebugLogger.Log("remote", $"Host identity updated: ip={_connectedHostIp}, hostName={_connectedHostName}");
                }
                else if (envelope.Type == RemoteMessageType.Heartbeat)
                {
                    var heartbeat = RemoteProtocolJson.DeserializePayload<HeartbeatPayload>(envelope) ?? new HeartbeatPayload();
                    RaiseHostTelemetry(heartbeat);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (TryGetRecentHostErrorForStatusGuard(ex, out string recentHostError))
            {
                DebugLogger.LogAlways("remote", $"Client receive loop terminated after host error; preserving status 'Host error: {recentHostError}' (exception: {ex.Message})");
                return;
            }

            RaiseStatus($"Connection lost: {ex.Message}");
            DebugLogger.LogAlways("remote", $"Client receive loop terminated: {ex.Message}");
        }
    }

    private void RememberHostError(string message)
    {
        lock (_sync)
        {
            _lastHostErrorMessage = message ?? "Unknown";
            _lastHostErrorUtc = DateTime.UtcNow;
        }
    }

    private void ClearRecentHostError()
    {
        lock (_sync)
        {
            _lastHostErrorMessage = "";
            _lastHostErrorUtc = DateTime.MinValue;
        }
    }

    private bool TryGetRecentHostErrorForStatusGuard(Exception ex, out string message)
    {
        message = "";

        if (!IsLikelyEofDisconnect(ex))
        {
            return false;
        }

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_lastHostErrorMessage))
            {
                return false;
            }

            if (DateTime.UtcNow - _lastHostErrorUtc > HostErrorStatusGuardWindow)
            {
                return false;
            }

            message = _lastHostErrorMessage;
            return true;
        }
    }

    private static bool IsLikelyEofDisconnect(Exception ex)
    {
        if (ex is System.IO.EndOfStreamException)
        {
            return true;
        }

        string message = ex?.Message ?? "";
        if (message.IndexOf("end of the stream", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (message.IndexOf("read beyond end", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var heartbeat = new HeartbeatPayload
                {
                    SenderTickMs = Environment.TickCount64
                };

                await SendControlMessageAsync(RemoteMessageType.Heartbeat, heartbeat, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLogger.Log("remote", $"Client heartbeat loop terminated: {ex.Message}");
        }
    }

    private void RaiseStatus(string status)
    {
        ConnectionStatusChanged?.Invoke(this, status);
    }

    private void RaiseHostIdentity(string hostIp, string hostName)
    {
        HostIdentityChanged?.Invoke(this, new RemoteHostIdentityEventArgs
        {
            HostIp = hostIp ?? "",
            HostName = hostName ?? ""
        });
    }

    private void RaiseHostTelemetry(HeartbeatPayload heartbeat)
    {
        HostTelemetryChanged?.Invoke(this, new RemoteHostTelemetryEventArgs
        {
            LastLagMs = heartbeat?.LastLagMs ?? 0,
            AvgLagMs = heartbeat?.AvgLagMs ?? 0,
            MaxLagMs = heartbeat?.MaxLagMs ?? 0,
            JitterMs = heartbeat?.JitterMs ?? 0,
            AcceptedFrames = heartbeat?.AcceptedFrames ?? 0,
            AcceptedFramesLast60s = heartbeat?.AcceptedFramesLast60s ?? 0,
            DroppedStaleFrames = heartbeat?.DroppedStaleFrames ?? 0
        });
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
        _sendLock.Dispose();
    }
}
