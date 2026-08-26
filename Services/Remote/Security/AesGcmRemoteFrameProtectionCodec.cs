using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public sealed class AesGcmRemoteFrameProtectionCodec : IRemoteFrameProtectionCodec
{
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly byte[] _sendNoncePrefix;
    private readonly byte[] _receiveNoncePrefix;
    private ulong _sendSequence;
    private ulong _lastReceiveSequence;

    public AesGcmRemoteFrameProtectionCodec(byte[] sendKey, byte[] receiveKey, byte[] sendNoncePrefix, byte[] receiveNoncePrefix)
    {
        _sendKey = sendKey ?? throw new ArgumentNullException(nameof(sendKey));
        _receiveKey = receiveKey ?? throw new ArgumentNullException(nameof(receiveKey));
        _sendNoncePrefix = sendNoncePrefix ?? throw new ArgumentNullException(nameof(sendNoncePrefix));
        _receiveNoncePrefix = receiveNoncePrefix ?? throw new ArgumentNullException(nameof(receiveNoncePrefix));

        if (_sendKey.Length != 32 || _receiveKey.Length != 32)
        {
            throw new ArgumentException("AES-GCM codec requires 32-byte keys.");
        }

        if (_sendNoncePrefix.Length != 4 || _receiveNoncePrefix.Length != 4)
        {
            throw new ArgumentException("AES-GCM nonce prefixes must be 4 bytes.");
        }
    }

    public Task<RemoteEncryptedFrame> EncryptAsync(byte[] plaintextFrame, CancellationToken ct)
    {
        if (plaintextFrame == null)
        {
            throw new ArgumentNullException(nameof(plaintextFrame));
        }

        ulong sequence = unchecked(++_sendSequence);
        byte[] nonce = BuildNonce(_sendNoncePrefix, sequence);
        byte[] aad = BuildAad(sequence);
        byte[] ciphertext = new byte[plaintextFrame.Length];
        byte[] tag = new byte[16];

        using var aes = new AesGcm(_sendKey, 16);
        aes.Encrypt(nonce, plaintextFrame, ciphertext, tag, aad);

        var frame = new RemoteEncryptedFrame
        {
            Sequence = sequence,
            Nonce = nonce,
            Ciphertext = ciphertext,
            AuthTag = tag,
        };

        return Task.FromResult(frame);
    }

    public Task<byte[]> DecryptAsync(RemoteEncryptedFrame encryptedFrame, CancellationToken ct)
    {
        if (encryptedFrame == null)
        {
            throw new ArgumentNullException(nameof(encryptedFrame));
        }

        if (encryptedFrame.Sequence <= _lastReceiveSequence)
        {
            throw new CryptographicException("Replay detected: sequence is not strictly increasing.");
        }

        byte[] expectedNonce = BuildNonce(_receiveNoncePrefix, encryptedFrame.Sequence);
        if (!CryptographicOperations.FixedTimeEquals(expectedNonce, encryptedFrame.Nonce ?? Array.Empty<byte>()))
        {
            throw new CryptographicException("Encrypted frame nonce mismatch.");
        }

        byte[] ciphertext = encryptedFrame.Ciphertext ?? Array.Empty<byte>();
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] aad = BuildAad(encryptedFrame.Sequence);

        using var aes = new AesGcm(_receiveKey, 16);
        aes.Decrypt(expectedNonce, ciphertext, encryptedFrame.AuthTag ?? Array.Empty<byte>(), plaintext, aad);

        _lastReceiveSequence = encryptedFrame.Sequence;
        return Task.FromResult(plaintext);
    }

    private static byte[] BuildNonce(byte[] prefix, ulong sequence)
    {
        byte[] nonce = new byte[12];
        Buffer.BlockCopy(prefix, 0, nonce, 0, 4);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }

    private static byte[] BuildAad(ulong sequence)
    {
        byte[] aad = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(aad, sequence);
        return aad;
    }
}
