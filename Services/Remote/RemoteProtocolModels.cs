using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetKeyer.Helpers;

namespace NetKeyer.Services.Remote;

public enum RemoteMessageType
{
    Hello,
    Auth,
    SecureHandshakeHello,
    SecureHandshakeResponse,
    SecureFrame,
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
    public string AppVersion { get; set; } = AppReleaseInfo.Revision;
    public string Callsign { get; set; } = "";
    public string HostName { get; set; } = "";
}

public class AuthPayload
{
    public string Token { get; set; } = "";
}

public class SecureHandshakeHelloPayload
{
    public int SecureProtocolVersion { get; set; } = 1;
    public string Suite { get; set; } = "P256+ECDSA+HKDF-SHA256+AES-GCM";
    public string IdentityKeyId { get; set; } = "";
    public byte[] IdentityPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();
    public long SentAtUnixMs { get; set; }
}

public class SecureHandshakeResponsePayload
{
    public int SecureProtocolVersion { get; set; } = 1;
    public string Suite { get; set; } = "P256+ECDSA+HKDF-SHA256+AES-GCM";
    public string SessionId { get; set; } = "";
    public string IdentityKeyId { get; set; } = "";
    public byte[] IdentityPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] EphemeralPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] TranscriptSignature { get; set; } = Array.Empty<byte>();
    public long SentAtUnixMs { get; set; }
}

public class SecureFramePayload
{
    public ulong Sequence { get; set; }
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] AuthTag { get; set; } = Array.Empty<byte>();
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
    public double HandshakeDurationMs { get; set; }
    public double LastLagMs { get; set; }
    public double P50LagMs { get; set; }
    public double P95LagMs { get; set; }
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
