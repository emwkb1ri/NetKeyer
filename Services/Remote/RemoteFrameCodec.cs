using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote;

public static class RemoteFrameCodec
{
    public static byte[] SerializeEnvelope(RemoteMessageEnvelope envelope)
    {
        if (envelope == null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        string json = JsonSerializer.Serialize(envelope, RemoteProtocolJson.SerializerOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    public static RemoteMessageEnvelope DeserializeEnvelope(byte[] payload)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        string json = Encoding.UTF8.GetString(payload);
        var envelope = JsonSerializer.Deserialize<RemoteMessageEnvelope>(json, RemoteProtocolJson.SerializerOptions);
        if (envelope == null)
        {
            throw new InvalidDataException("Could not deserialize remote message envelope");
        }

        return envelope;
    }

    public static async Task WriteEnvelopeAsync(Stream stream, RemoteMessageEnvelope envelope, CancellationToken ct)
    {
        byte[] payload = SerializeEnvelope(envelope);

        if (payload.Length > RemoteDefaults.MaxFrameBytes)
        {
            throw new InvalidOperationException($"Frame too large: {payload.Length} bytes");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<RemoteMessageEnvelope> ReadEnvelopeAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > RemoteDefaults.MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid frame length: {length}");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);

        return DeserializeEnvelope(payload);
    }
}
