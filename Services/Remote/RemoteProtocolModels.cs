using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetKeyer.Services.Remote;

public enum RemoteMessageType
{
    Hello,
    Auth,
    PaddleState,
    Heartbeat,
    Disconnect,
    Error
}

public class RemoteMessageEnvelope
{
    public int ProtocolVersion { get; set; } = RemoteDefaults.ProtocolVersion;
    public RemoteMessageType Type { get; set; }
    public long Sequence { get; set; }
    public long SentAtUnixMs { get; set; }
    public JsonElement Payload { get; set; }
}

public static class RemoteProtocolJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static RemoteMessageEnvelope CreateEnvelope<TPayload>(RemoteMessageType type, long sequence, TPayload payload)
    {
        return new RemoteMessageEnvelope
        {
            Type = type,
            Sequence = sequence,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(payload, SerializerOptions)
        };
    }

    public static TPayload DeserializePayload<TPayload>(RemoteMessageEnvelope envelope)
    {
        return envelope.Payload.Deserialize<TPayload>(SerializerOptions);
    }
}

public class HelloPayload
{
    public string AppName { get; set; } = "NetKeyer";
    public string AppVersion { get; set; } = "dev";
    public string Callsign { get; set; } = "";
    public string HostName { get; set; } = "";
}

public class AuthPayload
{
    public string Token { get; set; } = "";
}

public class PaddleStatePayload
{
    public bool LeftPaddle { get; set; }
    public bool RightPaddle { get; set; }
    public bool StraightKey { get; set; }
    public bool Ptt { get; set; }
    public long SenderTickMs { get; set; }
}

public class HeartbeatPayload
{
    public long SenderTickMs { get; set; }
    public bool IsTransmitModeCW { get; set; } = true;
    public string TransmitMode { get; set; } = "CW";
    public double LastLagMs { get; set; }
    public double AvgLagMs { get; set; }
    public double MaxLagMs { get; set; }
    public double JitterMs { get; set; }
    public long AcceptedFrames { get; set; }
    public long AcceptedFramesLast60s { get; set; }
    public long DroppedStaleFrames { get; set; }
}

public class ErrorPayload
{
    public string Message { get; set; } = "";
}
