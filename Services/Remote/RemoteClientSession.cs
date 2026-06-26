using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public class RemoteClientSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _requiredToken;
    private readonly string _hostName;
    private readonly Func<string, HeartbeatPayload> _heartbeatPayloadProvider;
    private bool _isAuthenticated;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string RemoteEndpoint { get; }
    public string RemoteIp { get; }
    public string Callsign { get; private set; } = "";

    public event EventHandler<RemotePaddleStateEventArgs> PaddleStateReceived;
    public event EventHandler<RemoteClientSession> SessionClosed;
    public event EventHandler<RemoteClientSession> SessionMetadataChanged;

    public RemoteClientSession(TcpClient client, string requiredToken, string hostName, Func<string, HeartbeatPayload> heartbeatPayloadProvider)
    {
        _client = client;
        _stream = _client.GetStream();
        _requiredToken = requiredToken ?? "";
        _hostName = hostName ?? "";
        _heartbeatPayloadProvider = heartbeatPayloadProvider;
        _isAuthenticated = string.IsNullOrWhiteSpace(_requiredToken);
        RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        RemoteIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await SendHelloAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var envelope = await RemoteFrameCodec.ReadEnvelopeAsync(_stream, ct);

                switch (envelope.Type)
                {
                    case RemoteMessageType.Hello:
                        await HandleHelloAsync(envelope);
                        break;

                    case RemoteMessageType.Auth:
                        await HandleAuthAsync(envelope, ct);
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
            DebugLogger.Log("remote", $"Session {ClientId} closed with error: {ex.Message}");
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

    private async Task HandleAuthAsync(RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        var auth = RemoteProtocolJson.DeserializePayload<AuthPayload>(envelope);
        _isAuthenticated = string.IsNullOrWhiteSpace(_requiredToken) || auth?.Token == _requiredToken;

        if (!_isAuthenticated)
        {
            await SendErrorAsync("Authentication failed", ct);
            try { _client.Close(); } catch { }
        }
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
        if (apparentAgeMs < 0)
        {
            apparentAgeMs = 0;
        }

        PaddleStateReceived?.Invoke(this, new RemotePaddleStateEventArgs
        {
            ClientId = ClientId,
            RemoteEndpoint = RemoteEndpoint,
            State = state,
            Sequence = envelope.Sequence,
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
            await RemoteFrameCodec.WriteEnvelopeAsync(_stream, envelope, ct);
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
            await RemoteFrameCodec.WriteEnvelopeAsync(_stream, envelope, ct);
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
            await RemoteFrameCodec.WriteEnvelopeAsync(_stream, envelope, ct);
        }
        catch
        {
            // Ignore heartbeat response failures; session lifecycle handles socket errors.
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
