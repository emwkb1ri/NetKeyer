using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;
using NetKeyer.Services.Remote.Security;

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
    private IRemoteFrameProtectionCodec _frameProtectionCodec;
    private bool _ciphertextValidationEnabled;

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
        bool isRelayTransport = !string.IsNullOrWhiteSpace(options.RelaySessionId) && !string.IsNullOrWhiteSpace(options.RelayRole);

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
            _frameProtectionCodec = null;
            _ciphertextValidationEnabled = options.ValidateRelayCiphertext;
        }

        if (options.EnableSecureTransport)
        {
            try
            {
                var identityProvider = new LocalFileRemoteIdentityKeyProvider("client");
                var negotiator = new SimpleRemoteSecureSessionNegotiator(identityProvider);
                RemoteHandshakeResult handshake = await negotiator.NegotiateClientAsync(stream, options.TargetHost, _internalCts.Token);
                _frameProtectionCodec = new AesGcmRemoteFrameProtectionCodec(
                    handshake.SendKey,
                    handshake.ReceiveKey,
                    handshake.SendNoncePrefix,
                    handshake.ReceiveNoncePrefix);
                DebugLogger.LogAlways("remote", $"Client secure handshake completed: session={handshake.SessionId} suite={handshake.SelectedSuite}");
            }
            catch (Exception ex)
            {
                if (options.RequireSecureTransport || _ciphertextValidationEnabled)
                {
                    throw new InvalidOperationException(
                        BuildUserFacingSecurityDiagnostic($"Secure transport handshake failed: {ex.Message}"),
                        ex);
                }

                DebugLogger.LogAlways("remote", $"Secure transport handshake failed; falling back to plaintext mode: {ex.Message}");
                _frameProtectionCodec = null;
            }
        }

        if (_ciphertextValidationEnabled && _frameProtectionCodec == null)
        {
            throw new InvalidOperationException(BuildUserFacingSecurityDiagnostic("Ciphertext validation is enabled, but secure transport was not established."));
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
            _frameProtectionCodec = null;
            _ciphertextValidationEnabled = false;
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
            await WriteEnvelopeAsync(stream, envelope, ct);
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

                var envelope = await ReadEnvelopeAsync(stream, ct);
                if (envelope == null)
                {
                    continue;
                }

                if (envelope.Type == RemoteMessageType.Error)
                {
                    var payload = RemoteProtocolJson.DeserializePayload<ErrorPayload>(envelope);
                    string hostErrorMessage = payload?.Message ?? "Unknown";
                    string userFacingHostError = BuildUserFacingSecurityDiagnostic(hostErrorMessage);
                    RememberHostError(userFacingHostError);
                    RaiseStatus($"Host error: {userFacingHostError}");
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

            string userFacing = BuildUserFacingSecurityDiagnostic(ex.Message);
            RaiseStatus($"Connection lost: {userFacing}");
            DebugLogger.LogAlways("remote", $"Client receive loop terminated: {ex.Message}");
        }
    }

    internal static string BuildUserFacingSecurityDiagnostic(string detail)
    {
        string message = detail ?? string.Empty;
        string lower = message.ToLowerInvariant();

        if (lower.Contains("shared token mismatch") || lower.Contains("missing shared token") || lower.Contains("authentication required"))
        {
            return "Authentication failed. Verify both sides use the same shared token and reconnect.";
        }

        if (lower.Contains("ciphertext validation") || lower.Contains("expected secure frame") || lower.Contains("received secure frame while secure transport is disabled"))
        {
            return "Security policy blocked the connection because encrypted frame requirements were not met.";
        }

        if (lower.Contains("secure transport handshake failed")
            || lower.Contains("secure transport was not established")
            || lower.Contains("downgrade")
            || lower.Contains("unsupported-upgrade")
            || lower.Contains("not allowed"))
        {
            return "Security policy blocked the connection because a required secure handshake could not be completed.";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return "Connection failed due to an unexpected transport error.";
        }

        return message;
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

    private async Task WriteEnvelopeAsync(NetworkStream stream, RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        if (_frameProtectionCodec == null)
        {
            await RemoteFrameCodec.WriteEnvelopeAsync(stream, envelope, ct);
            return;
        }

        byte[] plain = RemoteFrameCodec.SerializeEnvelope(envelope);
        RemoteEncryptedFrame encrypted = await _frameProtectionCodec.EncryptAsync(plain, ct);
        var secure = RemoteProtocolJson.CreateEnvelope(
            RemoteMessageType.SecureFrame,
            envelope.Sequence,
            new SecureFramePayload
            {
                Sequence = encrypted.Sequence,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext,
                AuthTag = encrypted.AuthTag,
            });
        await RemoteFrameCodec.WriteEnvelopeAsync(stream, secure, ct);
    }

    private async Task<RemoteMessageEnvelope> ReadEnvelopeAsync(NetworkStream stream, CancellationToken ct)
    {
        RemoteMessageEnvelope envelope = await RemoteFrameCodec.ReadEnvelopeAsync(stream, ct);
        if (envelope.Type != RemoteMessageType.SecureFrame)
        {
            ValidateCiphertextFrameType(envelope.Type, _ciphertextValidationEnabled, _frameProtectionCodec != null);
            return envelope;
        }

        if (_frameProtectionCodec == null)
        {
            throw new InvalidDataException("Received secure frame while secure transport is disabled.");
        }

        var payload = RemoteProtocolJson.DeserializePayload<SecureFramePayload>(envelope)
            ?? throw new InvalidDataException("Invalid secure frame payload");

        var encrypted = new RemoteEncryptedFrame
        {
            Sequence = payload.Sequence,
            Nonce = payload.Nonce,
            Ciphertext = payload.Ciphertext,
            AuthTag = payload.AuthTag,
        };

        byte[] plain = await _frameProtectionCodec.DecryptAsync(encrypted, ct);
        return RemoteFrameCodec.DeserializeEnvelope(plain);
    }

    internal static void ValidateCiphertextFrameType(RemoteMessageType receivedType, bool ciphertextValidationEnabled, bool secureTransportEstablished)
    {
        if (ciphertextValidationEnabled && secureTransportEstablished && receivedType != RemoteMessageType.SecureFrame)
        {
            throw new InvalidDataException(
                $"Ciphertext validation failed: expected secure frame, received '{receivedType}'.");
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
            IsTransmitModeCW = heartbeat?.IsTransmitModeCW ?? true,
            TransmitMode = heartbeat?.TransmitMode ?? "CW",
            HandshakeDurationMs = heartbeat?.HandshakeDurationMs ?? 0,
            LastLagMs = heartbeat?.LastLagMs ?? 0,
            P50LagMs = heartbeat?.P50LagMs ?? 0,
            P95LagMs = heartbeat?.P95LagMs ?? 0,
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
