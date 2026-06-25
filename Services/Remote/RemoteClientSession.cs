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
    private bool _isAuthenticated;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string RemoteEndpoint { get; }

    public event EventHandler<RemotePaddleStateEventArgs> PaddleStateReceived;
    public event EventHandler<RemoteClientSession> SessionClosed;

    public RemoteClientSession(TcpClient client, string requiredToken)
    {
        _client = client;
        _stream = _client.GetStream();
        _requiredToken = requiredToken ?? "";
        _isAuthenticated = string.IsNullOrWhiteSpace(_requiredToken);
        RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await RemoteFrameCodec.ReadEnvelopeAsync(_stream, ct);

                switch (envelope.Type)
                {
                    case RemoteMessageType.Hello:
                        DebugLogger.Log("remote", $"Session {ClientId} hello from {RemoteEndpoint}");
                        break;

                    case RemoteMessageType.Auth:
                        await HandleAuthAsync(envelope, ct);
                        break;

                    case RemoteMessageType.PaddleState:
                        await HandlePaddleStateAsync(envelope, ct);
                        break;

                    case RemoteMessageType.Heartbeat:
                        // no-op for now
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

        PaddleStateReceived?.Invoke(this, new RemotePaddleStateEventArgs
        {
            ClientId = ClientId,
            RemoteEndpoint = RemoteEndpoint,
            State = state,
            ReceivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
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

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
