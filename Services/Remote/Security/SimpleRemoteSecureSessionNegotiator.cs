using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public sealed class SimpleRemoteSecureSessionNegotiator : IRemoteSecureSessionNegotiator
{
    private const string SuiteName = "P256+ECDSA+HKDF-SHA256+AES-GCM";
    private readonly IRemoteIdentityKeyProvider _identityKeyProvider;

    public SimpleRemoteSecureSessionNegotiator(IRemoteIdentityKeyProvider identityKeyProvider)
    {
        _identityKeyProvider = identityKeyProvider ?? throw new ArgumentNullException(nameof(identityKeyProvider));
    }

    public async Task<RemoteHandshakeResult> NegotiateClientAsync(Stream transport, string expectedHostId, CancellationToken ct)
    {
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        RemoteIdentityKeyPair clientIdentity = await _identityKeyProvider.GetOrCreateIdentityAsync(ct);

        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new SecureHandshakeHelloPayload
        {
            SecureProtocolVersion = RemoteSecureProtocolDefaults.SecureProtocolVersion,
            Suite = SuiteName,
            IdentityKeyId = clientIdentity.KeyId,
            IdentityPublicKey = clientIdentity.PublicKey,
            EphemeralPublicKey = clientEcdh.ExportSubjectPublicKeyInfo(),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var helloEnvelope = RemoteProtocolJson.CreateEnvelope(RemoteMessageType.SecureHandshakeHello, 0, hello);
        await RemoteFrameCodec.WriteEnvelopeAsync(transport, helloEnvelope, ct);

        RemoteMessageEnvelope responseEnvelope = await RemoteFrameCodec.ReadEnvelopeAsync(transport, ct);
        if (responseEnvelope.Type != RemoteMessageType.SecureHandshakeResponse)
        {
            throw new InvalidDataException($"Expected secure handshake response, got {responseEnvelope.Type}");
        }

        var response = RemoteProtocolJson.DeserializePayload<SecureHandshakeResponsePayload>(responseEnvelope)
            ?? throw new InvalidDataException("Invalid secure handshake response payload");

        EnsureVersionAccepted(
            response.SecureProtocolVersion,
            RemoteSecureProtocolDefaults.SecureProtocolVersion,
            "host secure handshake response");
        EnsureSuiteAccepted(response.Suite, SuiteName, "host secure handshake response");

        if (string.IsNullOrWhiteSpace(response.SessionId))
        {
            throw new InvalidDataException("Secure handshake response did not include a session ID.");
        }

        byte[] transcriptHash = ComputeTranscriptHash(hello, response);

        using (var hostIdentity = ECDsa.Create())
        {
            hostIdentity.ImportSubjectPublicKeyInfo(response.IdentityPublicKey, out _);
            bool valid = hostIdentity.VerifyHash(transcriptHash, response.TranscriptSignature);
            if (!valid)
            {
                throw new CryptographicException("Secure handshake transcript signature is invalid.");
            }
        }

        using var hostEcdh = ECDiffieHellman.Create();
        hostEcdh.ImportSubjectPublicKeyInfo(response.EphemeralPublicKey, out _);
        byte[] sharedSecret = clientEcdh.DeriveKeyMaterial(hostEcdh.PublicKey);

        byte[] keyMaterial = DeriveKeyMaterial(sharedSecret, transcriptHash, 72);
        return new RemoteHandshakeResult
        {
            SessionId = response.SessionId,
            IsDirectPath = false,
            SelectedSuite = response.Suite,
            SendKey = Slice(keyMaterial, 0, 32),
            ReceiveKey = Slice(keyMaterial, 32, 32),
            SendNoncePrefix = Slice(keyMaterial, 64, 4),
            ReceiveNoncePrefix = Slice(keyMaterial, 68, 4),
        };
    }

    public async Task<RemoteHandshakeResult> NegotiateHostAsync(Stream transport, string expectedClientId, CancellationToken ct)
    {
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        RemoteIdentityKeyPair hostIdentity = await _identityKeyProvider.GetOrCreateIdentityAsync(ct);

        RemoteMessageEnvelope helloEnvelope = await RemoteFrameCodec.ReadEnvelopeAsync(transport, ct);
        if (helloEnvelope.Type != RemoteMessageType.SecureHandshakeHello)
        {
            throw new InvalidDataException($"Expected secure handshake hello, got {helloEnvelope.Type}");
        }

        var hello = RemoteProtocolJson.DeserializePayload<SecureHandshakeHelloPayload>(helloEnvelope)
            ?? throw new InvalidDataException("Invalid secure handshake hello payload");

        EnsureVersionAccepted(
            hello.SecureProtocolVersion,
            RemoteSecureProtocolDefaults.SecureProtocolVersion,
            "client secure handshake hello");
        EnsureSuiteAccepted(hello.Suite, SuiteName, "client secure handshake hello");

        using var hostEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var response = new SecureHandshakeResponsePayload
        {
            SecureProtocolVersion = RemoteSecureProtocolDefaults.SecureProtocolVersion,
            Suite = SuiteName,
            SessionId = Guid.NewGuid().ToString("N"),
            IdentityKeyId = hostIdentity.KeyId,
            IdentityPublicKey = hostIdentity.PublicKey,
            EphemeralPublicKey = hostEcdh.ExportSubjectPublicKeyInfo(),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TranscriptSignature = Array.Empty<byte>(),
        };

        byte[] transcriptHash = ComputeTranscriptHash(hello, response);
        using (var signer = ECDsa.Create())
        {
            signer.ImportPkcs8PrivateKey(hostIdentity.PrivateKey, out _);
            response.TranscriptSignature = signer.SignHash(transcriptHash);
        }

        var responseEnvelope = RemoteProtocolJson.CreateEnvelope(RemoteMessageType.SecureHandshakeResponse, 0, response);
        await RemoteFrameCodec.WriteEnvelopeAsync(transport, responseEnvelope, ct);

        using var clientEcdh = ECDiffieHellman.Create();
        clientEcdh.ImportSubjectPublicKeyInfo(hello.EphemeralPublicKey, out _);
        byte[] sharedSecret = hostEcdh.DeriveKeyMaterial(clientEcdh.PublicKey);

        byte[] keyMaterial = DeriveKeyMaterial(sharedSecret, transcriptHash, 72);
        return new RemoteHandshakeResult
        {
            SessionId = response.SessionId,
            IsDirectPath = false,
            SelectedSuite = response.Suite,
            SendKey = Slice(keyMaterial, 32, 32),
            ReceiveKey = Slice(keyMaterial, 0, 32),
            SendNoncePrefix = Slice(keyMaterial, 68, 4),
            ReceiveNoncePrefix = Slice(keyMaterial, 64, 4),
        };
    }

    private static byte[] ComputeTranscriptHash(SecureHandshakeHelloPayload hello, SecureHandshakeResponsePayload response)
    {
        var canonical = new
        {
            HelloProtocolVersion = hello.SecureProtocolVersion,
            HelloSuite = hello.Suite,
            HelloIdentityKeyId = hello.IdentityKeyId,
            HelloIdentityPublicKey = hello.IdentityPublicKey,
            HelloEphemeralPublicKey = hello.EphemeralPublicKey,
            ResponseProtocolVersion = response.SecureProtocolVersion,
            ResponseSuite = response.Suite,
            ResponseSessionId = response.SessionId,
            ResponseIdentityKeyId = response.IdentityKeyId,
            ResponseIdentityPublicKey = response.IdentityPublicKey,
            ResponseEphemeralPublicKey = response.EphemeralPublicKey,
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, RemoteProtocolJson.SerializerOptions);
        return SHA256.HashData(bytes);
    }

    private static byte[] DeriveKeyMaterial(byte[] ikm, byte[] info, int length)
    {
        byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("netkeyer-remote-phase3"));
        byte[] prk;
        using (var hmac = new HMACSHA256(salt))
        {
            prk = hmac.ComputeHash(ikm);
        }

        byte[] output = new byte[length];
        byte[] previous = Array.Empty<byte>();
        int offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            using var hmac = new HMACSHA256(prk);
            byte[] input = new byte[previous.Length + info.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(info, 0, input, previous.Length, info.Length);
            input[input.Length - 1] = counter;
            previous = hmac.ComputeHash(input);

            int take = Math.Min(previous.Length, length - offset);
            Buffer.BlockCopy(previous, 0, output, offset, take);
            offset += take;
            counter++;
        }

        return output;
    }

    private static byte[] Slice(byte[] source, int offset, int length)
    {
        byte[] result = new byte[length];
        Buffer.BlockCopy(source, offset, result, 0, length);
        return result;
    }

    internal static void EnsureVersionAccepted(int offered, int expected, string stage)
    {
        if (offered == expected)
        {
            return;
        }

        string direction = offered < expected ? "downgrade" : "unsupported-upgrade";
        throw new InvalidDataException(
            $"Rejected {stage}: protocol version {offered} is not allowed (expected {expected}, reason={direction}).");
    }

    internal static void EnsureSuiteAccepted(string offered, string expected, string stage)
    {
        if (string.Equals((offered ?? string.Empty).Trim(), expected, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"Rejected {stage}: crypto suite '{offered}' is not allowed (expected '{expected}').");
    }
}
