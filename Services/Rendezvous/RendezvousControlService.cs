using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Rendezvous;

public sealed class RendezvousControlService : IRendezvousControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RendezvousHostRegistrationSession> RegisterHostAsync(RendezvousHostRegistrationOptions options, CancellationToken ct)
    {
        ValidateHostOptions(options);

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(BuildEndpoint(options.ServerUrl, "host"), ct);

        var payload = new
        {
            protocol_version = 1,
            type = "register_host",
            host_id = options.HostId,
            max_clients = Math.Max(1, Math.Min(5, options.MaxClients)),
            metadata = options.Metadata ?? new Dictionary<string, object>()
        };

        await SendJsonAsync(ws, payload, ct);

        var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = Task.Run(() => HostReceiveLoopAsync(ws, options, receiveCts.Token), CancellationToken.None);

        return new RendezvousHostRegistrationSession(async () =>
        {
            receiveCts.Cancel();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "host_shutdown", CancellationToken.None);
                }
            }
            catch
            {
                // Best effort close.
            }
            finally
            {
                ws.Dispose();
                receiveCts.Dispose();
            }

            try
            {
                await receiveTask;
            }
            catch
            {
                // Ignore loop cancellation/teardown errors.
            }
        });
    }

    public async Task<RendezvousClientConnectionSession> ConnectClientAsync(RendezvousClientConnectOptions options, CancellationToken ct)
    {
        ValidateClientOptions(options);

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(BuildEndpoint(options.ServerUrl, "client"), ct);

        await SendJsonAsync(ws, new
        {
            protocol_version = 1,
            type = "register_client",
            client_id = options.ClientId
        }, ct);

        await SendJsonAsync(ws, new
        {
            protocol_version = 1,
            type = "connect_request",
            client_id = options.ClientId,
            host_id = options.HostId
        }, ct);

        RendezvousResolvedEndpoint endpoint = await WaitForHostEndpointAsync(ws, null, ct);

        var session = new RendezvousClientConnectionSession(
            endpoint,
            (success, token) => ReportPunchResultAsync(ws, options.ClientId, options.HostId, endpoint.SessionId, success, token),
            async () =>
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_shutdown", CancellationToken.None);
                    }
                }
                catch
                {
                    // Best effort close.
                }
                finally
                {
                    ws.Dispose();
                }
            });

        session.ControlSocket = ws;
        session.RelayRequested = false;

        return session;
    }

    public async Task<IReadOnlyList<RendezvousHostSummary>> ListHostsAsync(RendezvousHostListRequestOptions options, CancellationToken ct)
    {
        ValidateListOptions(options);

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(BuildEndpoint(options.ServerUrl, "client"), ct);

        try
        {
            await SendJsonAsync(ws, new
            {
                protocol_version = 1,
                type = "register_client",
                client_id = options.ClientId
            }, ct);

            await SendJsonAsync(ws, new
            {
                protocol_version = 1,
                type = "list_hosts"
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                JsonElement msg = await ReceiveJsonAsync(ws, ct);
                string type = msg.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string code = msg.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "error" : "error";
                    string message = msg.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Rendezvous error" : "Rendezvous error";
                    throw new InvalidOperationException($"Rendezvous error ({code}): {message}");
                }

                if (!string.Equals(type, "host_list", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var results = new List<RendezvousHostSummary>();
                if (msg.TryGetProperty("hosts", out var hostsEl) && hostsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement hostEl in hostsEl.EnumerateArray())
                    {
                        string hostId = hostEl.TryGetProperty("host_id", out var hidEl) ? hidEl.GetString() ?? "" : "";
                        int currentClients = hostEl.TryGetProperty("current_clients", out var ccEl) && ccEl.TryGetInt32(out var ccVal) ? ccVal : 0;
                        int maxClients = hostEl.TryGetProperty("max_clients", out var mcEl) && mcEl.TryGetInt32(out var mcVal) ? mcVal : 0;

                        string name = "";
                        if (hostEl.TryGetProperty("metadata", out var metadataEl)
                            && metadataEl.ValueKind == JsonValueKind.Object
                            && metadataEl.TryGetProperty("name", out var nameEl))
                        {
                            name = nameEl.GetString() ?? "";
                        }

                        if (!string.IsNullOrWhiteSpace(hostId))
                        {
                            results.Add(new RendezvousHostSummary
                            {
                                HostId = hostId,
                                Name = name,
                                CurrentClients = currentClients,
                                MaxClients = maxClients
                            });
                        }
                    }
                }

                return new ReadOnlyCollection<RendezvousHostSummary>(results);
            }

            throw new OperationCanceledException("Timed out waiting for rendezvous host list.", ct);
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "list_complete", CancellationToken.None);
                }
            }
            catch
            {
            }

            ws.Dispose();
        }
    }

    private static async Task<RendezvousResolvedEndpoint> WaitForHostEndpointAsync(
        ClientWebSocket ws,
        RendezvousClientConnectionSession session,
        CancellationToken ct)
    {
        string hostIp = "";
        int hostPort = 0;
        string sessionId = "";
        bool sawStartPunch = false;

        while (!ct.IsCancellationRequested)
        {
            JsonElement msg = await ReceiveJsonAsync(ws, ct);
            string type = msg.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                string code = msg.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "error" : "error";
                string message = msg.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Rendezvous error" : "Rendezvous error";
                throw new InvalidOperationException($"Rendezvous error ({code}): {message}");
            }

            if (string.Equals(type, "use_relay", StringComparison.OrdinalIgnoreCase))
            {
                CaptureRelayMessage(session, msg);
            }

            if (string.Equals(type, "host_endpoint", StringComparison.OrdinalIgnoreCase))
            {
                hostIp = msg.GetProperty("host_public_ip").GetString() ?? "";
                hostPort = msg.GetProperty("host_public_port").GetInt32();
                sessionId = msg.GetProperty("session_id").GetString() ?? "";
            }
            else if (string.Equals(type, "start_punch", StringComparison.OrdinalIgnoreCase))
            {
                sawStartPunch = true;
                if (string.IsNullOrWhiteSpace(sessionId) && msg.TryGetProperty("session_id", out var sidProp))
                {
                    sessionId = sidProp.GetString() ?? "";
                }
            }

            if (!string.IsNullOrWhiteSpace(hostIp) && hostPort > 0 && !string.IsNullOrWhiteSpace(sessionId) && sawStartPunch)
            {
                return new RendezvousResolvedEndpoint
                {
                    HostPublicIp = hostIp,
                    HostPublicPort = hostPort,
                    SessionId = sessionId
                };
            }
        }

        throw new OperationCanceledException("Timed out waiting for rendezvous host endpoint.", ct);
    }

    private static async Task ReportPunchResultAsync(
        ClientWebSocket ws,
        string clientId,
        string hostId,
        string sessionId,
        bool success,
        CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        await SendJsonAsync(ws, new
        {
            protocol_version = 1,
            type = "punch_result",
            success,
            client_id = clientId,
            host_id = hostId,
            session_id = sessionId
        }, ct);
    }

    public async Task<bool> WaitForRelayAsync(RendezvousClientConnectionSession session, TimeSpan timeout, CancellationToken ct)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (session.HasRelayEndpoint)
        {
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                if (session.HasRelayEndpoint)
                {
                    return true;
                }

                if (session.ControlSocket == null || session.ControlSocket.State != WebSocketState.Open)
                {
                    return session.HasRelayEndpoint;
                }

                JsonElement msg = await ReceiveJsonAsync(session.ControlSocket, timeoutCts.Token);
                string type = msg.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                if (string.Equals(type, "use_relay", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureRelayMessage(session, msg);
                    return session.HasRelayEndpoint;
                }

                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string code = msg.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "error" : "error";
                    string message = msg.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Rendezvous error" : "Rendezvous error";
                    DebugLogger.Log("rendezvous", $"Client signaling error while waiting for relay ({code}): {message}");
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return session.HasRelayEndpoint;
        }

        return session.HasRelayEndpoint;
    }

    private static async Task HostReceiveLoopAsync(ClientWebSocket ws, RendezvousHostRegistrationOptions options, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                JsonElement msg = await ReceiveJsonAsync(ws, ct);
                string type = msg.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string code = msg.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "error" : "error";
                    string message = msg.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Rendezvous error" : "Rendezvous error";
                    DebugLogger.Log("rendezvous", $"Host signaling error ({code}): {message}");
                    continue;
                }

                if (string.Equals(type, "use_relay", StringComparison.OrdinalIgnoreCase))
                {
                    string relayHost = msg.TryGetProperty("relay_host", out var hostEl) ? hostEl.GetString() ?? "" : "";
                    int relayPort = msg.TryGetProperty("relay_port", out var portEl) && portEl.TryGetInt32(out var relayPortValue) ? relayPortValue : 0;
                    string sessionId = msg.TryGetProperty("session_id", out var sessionEl) ? sessionEl.GetString() ?? "" : "";

                    if (options?.OnUseRelayAsync != null && !string.IsNullOrWhiteSpace(relayHost) && relayPort > 0 && !string.IsNullOrWhiteSpace(sessionId))
                    {
                        await options.OnUseRelayAsync(relayHost, relayPort, sessionId);
                    }

                    continue;
                }

                DebugLogger.Log("rendezvous", $"Host signaling message: {type}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            DebugLogger.Log("rendezvous", $"Host signaling websocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log("rendezvous", $"Host signaling loop terminated: {ex.Message}");
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var stream = new System.IO.MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Remote websocket closed.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            stream.Position = 0;
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Uri BuildEndpoint(string serverUrl, string role)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("Rendezvous server URL is required.", nameof(serverUrl));
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("Rendezvous server URL is invalid.", nameof(serverUrl));
        }

        string scheme = baseUri.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new ArgumentException("Rendezvous server URL must use http(s) or ws(s).", nameof(serverUrl))
        };

        string basePath = baseUri.AbsolutePath.TrimEnd('/');
        string wsPath = $"{basePath}/ws/{role}";
        if (!wsPath.StartsWith("/"))
        {
            wsPath = "/" + wsPath;
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = wsPath,
            Query = string.Empty
        };

        return builder.Uri;
    }

    private static void ValidateHostOptions(RendezvousHostRegistrationOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.HostId))
        {
            throw new ArgumentException("Host ID is required for rendezvous registration.", nameof(options));
        }
    }

    private static void ValidateClientOptions(RendezvousClientConnectOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new ArgumentException("Client ID is required for rendezvous connect.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.HostId))
        {
            throw new ArgumentException("Host ID is required for rendezvous connect.", nameof(options));
        }
    }

    private static void ValidateListOptions(RendezvousHostListRequestOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ServerUrl))
        {
            throw new ArgumentException("Rendezvous server URL is required for host discovery.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new ArgumentException("Client ID is required for host discovery.", nameof(options));
        }
    }

    public void Dispose()
    {
    }

    private static void CaptureRelayMessage(RendezvousClientConnectionSession session, JsonElement msg)
    {
        if (session == null)
        {
            return;
        }

        session.RelayRequested = true;
        if (msg.TryGetProperty("relay_host", out var hostEl))
        {
            session.RelayHost = hostEl.GetString() ?? "";
        }

        if (msg.TryGetProperty("relay_port", out var portEl) && portEl.TryGetInt32(out var relayPort))
        {
            session.RelayPort = relayPort;
        }
    }
}
