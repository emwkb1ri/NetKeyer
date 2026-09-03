using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;
using NetKeyer.Services.Remote.Security;

namespace NetKeyer.Services.Remote;

public class RemoteClientSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _requiredToken;
    private readonly string _hostName;
    private readonly Func<string, HeartbeatPayload> _heartbeatPayloadProvider;
    private readonly bool _enableSecureTransport;
    private readonly bool _requireSecureTransport;
    private readonly bool _isRelayTransport;
    private readonly bool _validateRelayCiphertext;
    private IRemoteFrameProtectionCodec _frameProtectionCodec;
    private bool _isAuthenticated;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string RemoteEndpoint { get; }
    public string RemoteIp { get; }
    public string Callsign { get; private set; } = "";
    public double HandshakeDurationMs { get; private set; }

    public event EventHandler<RemotePaddleStateEventArgs> PaddleStateReceived;
    public event EventHandler<RemoteClientSession> SessionClosed;
    public event EventHandler<RemoteClientSession> SessionMetadataChanged;

    public RemoteClientSession(
        TcpClient client,
        string requiredToken,
        string hostName,
        Func<string, HeartbeatPayload> heartbeatPayloadProvider,
        bool enableSecureTransport,
        bool requireSecureTransport,
        bool isRelayTransport,
        bool validateRelayCiphertext)
    {
        _client = client;
        _stream = _client.GetStream();
        _requiredToken = requiredToken ?? "";
        _hostName = hostName ?? "";
        _heartbeatPayloadProvider = heartbeatPayloadProvider;
        _enableSecureTransport = enableSecureTransport;
        _requireSecureTransport = requireSecureTransport;
        _isRelayTransport = isRelayTransport;
        _validateRelayCiphertext = validateRelayCiphertext;
        _isAuthenticated = string.IsNullOrWhiteSpace(_requiredToken);
        RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        RemoteIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_enableSecureTransport)
            {
                try
                {
                    var identityProvider = new LocalFileRemoteIdentityKeyProvider("host");
                    var negotiator = new SimpleRemoteSecureSessionNegotiator(identityProvider);
                    RemoteHandshakeResult handshake = await negotiator.NegotiateHostAsync(_stream, ClientId, ct);
                    _frameProtectionCodec = new AesGcmRemoteFrameProtectionCodec(
                        handshake.SendKey,
                        handshake.ReceiveKey,
                        handshake.SendNoncePrefix,
                        handshake.ReceiveNoncePrefix);
                    HandshakeDurationMs = handshake.HandshakeDurationMs;
                    SessionMetadataChanged?.Invoke(this, this);
                    DebugLogger.LogAlways("remote", $"Host secure handshake completed: session={handshake.SessionId} suite={handshake.SelectedSuite} client={ClientId}");
                }
                catch (Exception ex)
                {
                    if (_requireSecureTransport || _validateRelayCiphertext)
                    {
                        DebugLogger.LogAlways("remote", $"Secure transport required but handshake failed for {ClientId}: {ex.Message}");
                        return;
                    }

                    DebugLogger.LogAlways("remote", $"Secure handshake failed for {ClientId}; falling back to plaintext mode: {ex.Message}");
                    _frameProtectionCodec = null;
                }
            }

            if (_validateRelayCiphertext && _frameProtectionCodec == null)
            {
                DebugLogger.LogAlways("remote", $"Rejecting session {ClientId}: ciphertext validation requires secure transport.");
                return;
            }

            await SendHelloAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var envelope = await ReadEnvelopeAsync(ct);

                switch (envelope.Type)
                {
                    case RemoteMessageType.Hello:
                        await HandleHelloAsync(envelope);
                        break;

                    case RemoteMessageType.Auth:
                        if (!await HandleAuthAsync(envelope, ct))
                        {
                            return;
                        }
                        break;

                    case RemoteMessageType.PaddleState:
                        await HandlePaddleStateAsync(envelope, ct);
                        break;

                    case RemoteMessageType.Heartbeat:
                        await HandleHeartbeatAsync(ct);
                        break;

                    case RemoteMessageType.Disconnect:
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLogger.LogAlways("remote", $"Session {ClientId} closed with error: {ex.Message}");
        }
        finally
        {
            SessionClosed?.Invoke(this, this);
        }
    }

    private async Task HandleHelloAsync(RemoteMessageEnvelope envelope)
    {
        var hello = RemoteProtocolJson.DeserializePayload<HelloPayload>(envelope);
        Callsign = hello?.Callsign ?? "";
        SessionMetadataChanged?.Invoke(this, this);
        DebugLogger.Log("remote", $"Session {ClientId} hello from {RemoteEndpoint}, callsign={Callsign}");
        await Task.CompletedTask;
    }

    private async Task<bool> HandleAuthAsync(RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        var auth = RemoteProtocolJson.DeserializePayload<AuthPayload>(envelope);
        string providedToken = auth?.Token ?? "";
        string expectedToken = _requiredToken ?? "";

        _isAuthenticated = string.IsNullOrWhiteSpace(expectedToken)
            || string.Equals(providedToken.Trim(), expectedToken.Trim(), StringComparison.Ordinal);

        if (!_isAuthenticated)
        {
            string refusalReason = string.IsNullOrWhiteSpace(providedToken)
                ? "missing shared token"
                : "shared token mismatch";

            DebugLogger.LogAlways("remote", $"Connection refused for session {ClientId} from {RemoteEndpoint}: {refusalReason}");
            await SendErrorAsync($"Connection refused: {refusalReason}", ct);
            return false;
        }

        DebugLogger.LogAlways("remote", $"Session {ClientId} authenticated from {RemoteEndpoint}");
        return true;
    }

    private async Task HandlePaddleStateAsync(RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        if (!_isAuthenticated)
        {
            await SendErrorAsync("Authentication required", ct);
            return;
        }

        var state = RemoteProtocolJson.DeserializePayload<PaddleStatePayload>(envelope);
        if (state == null)
        {
            await SendErrorAsync("Invalid paddle state payload", ct);
            return;
        }

        long receivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long apparentAgeMs = receivedAtUnixMs - envelope.SentAtUnixMs;
        long receivedAtTickMs = Environment.TickCount64;
        long senderTickMs = state.SenderTickMs;
        long senderTickAgeMs = receivedAtTickMs - senderTickMs;

        PaddleStateReceived?.Invoke(this, new RemotePaddleStateEventArgs
        {
            ClientId = ClientId,
            RemoteEndpoint = RemoteEndpoint,
            State = state,
            Sequence = envelope.Sequence,
            SenderTickMs = senderTickMs,
            ReceivedAtTickMs = receivedAtTickMs,
            SenderTickAgeMs = senderTickAgeMs,
            SentAtUnixMs = envelope.SentAtUnixMs,
            ReceivedAtUnixMs = receivedAtUnixMs,
            ApparentAgeMs = apparentAgeMs
        });
    }

    private async Task SendErrorAsync(string message, CancellationToken ct)
    {
        try
        {
            var envelope = RemoteProtocolJson.CreateEnvelope(RemoteMessageType.Error, 0, new ErrorPayload { Message = message });
            await WriteEnvelopeAsync(envelope, ct);
        }
        catch
        {
            // Ignore transport errors during error reporting
        }
    }

    private async Task SendHelloAsync(CancellationToken ct)
    {
        try
        {
            var envelope = RemoteProtocolJson.CreateEnvelope(RemoteMessageType.Hello, 0, new HelloPayload
            {
                HostName = _hostName
            });
            await WriteEnvelopeAsync(envelope, ct);
        }
        catch
        {
            // Ignore if hello cannot be sent; caller loop will handle stream state.
        }
    }

    private async Task HandleHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            HeartbeatPayload payload = _heartbeatPayloadProvider?.Invoke(ClientId) ?? new HeartbeatPayload();
            var envelope = RemoteProtocolJson.CreateEnvelope(RemoteMessageType.Heartbeat, 0, payload);
            await WriteEnvelopeAsync(envelope, ct);
        }
        catch
        {
            // Ignore heartbeat response failures; session lifecycle handles socket errors.
        }
    }

    private async Task WriteEnvelopeAsync(RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        if (_frameProtectionCodec == null)
        {
            await RemoteFrameCodec.WriteEnvelopeAsync(_stream, envelope, ct);
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

        await RemoteFrameCodec.WriteEnvelopeAsync(_stream, secure, ct);
    }

    private async Task<RemoteMessageEnvelope> ReadEnvelopeAsync(CancellationToken ct)
    {
        RemoteMessageEnvelope envelope = await RemoteFrameCodec.ReadEnvelopeAsync(_stream, ct);
        if (envelope.Type != RemoteMessageType.SecureFrame)
        {
            ValidateCiphertextFrameType(envelope.Type, _validateRelayCiphertext, _frameProtectionCodec != null);
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

    internal static void ValidateCiphertextFrameType(
        RemoteMessageType receivedType,
        bool validateCiphertext,
        bool secureTransportEstablished)
    {
        if (validateCiphertext && secureTransportEstablished && receivedType != RemoteMessageType.SecureFrame)
        {
            throw new InvalidDataException(
                $"Ciphertext validation failed: expected secure frame, received '{receivedType}'.");
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
