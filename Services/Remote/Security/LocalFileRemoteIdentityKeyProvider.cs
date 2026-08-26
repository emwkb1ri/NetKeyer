using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetKeyer.Services.Remote.Security;

public sealed class LocalFileRemoteIdentityKeyProvider : IRemoteIdentityKeyProvider
{
    private readonly string _filePath;

    public LocalFileRemoteIdentityKeyProvider(string roleTag)
    {
        string safeRole = string.IsNullOrWhiteSpace(roleTag) ? "default" : roleTag.Trim().ToLowerInvariant();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetKeyer", "security");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, $"remote-identity-{safeRole}.json");
    }

    public async Task<RemoteIdentityKeyPair> GetOrCreateIdentityAsync(CancellationToken ct)
    {
        if (File.Exists(_filePath))
        {
            string existing = await File.ReadAllTextAsync(_filePath, ct);
            var stored = JsonSerializer.Deserialize<StoredIdentity>(existing) ?? throw new InvalidOperationException("Invalid identity store format");
            return new RemoteIdentityKeyPair
            {
                KeyId = stored.KeyId ?? string.Empty,
                Algorithm = stored.Algorithm ?? "ECDSA-P256",
                PublicKey = Convert.FromBase64String(stored.PublicKey ?? string.Empty),
                PrivateKey = Convert.FromBase64String(stored.PrivateKey ?? string.Empty),
            };
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKey = ecdsa.ExportPkcs8PrivateKey();
        byte[] publicKey = ecdsa.ExportSubjectPublicKeyInfo();

        var identity = new RemoteIdentityKeyPair
        {
            KeyId = Guid.NewGuid().ToString("N"),
            Algorithm = "ECDSA-P256",
            PublicKey = publicKey,
            PrivateKey = privateKey,
        };

        var storedIdentity = new StoredIdentity
        {
            KeyId = identity.KeyId,
            Algorithm = identity.Algorithm,
            PublicKey = Convert.ToBase64String(identity.PublicKey),
            PrivateKey = Convert.ToBase64String(identity.PrivateKey),
        };

        string json = JsonSerializer.Serialize(storedIdentity, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json, ct);
        return identity;
    }

    public async Task<RemotePublicIdentity> GetPublicIdentityAsync(CancellationToken ct)
    {
        RemoteIdentityKeyPair pair = await GetOrCreateIdentityAsync(ct);
        return new RemotePublicIdentity
        {
            KeyId = pair.KeyId,
            Algorithm = pair.Algorithm,
            PublicKey = pair.PublicKey,
        };
    }

    private sealed class StoredIdentity
    {
        public string KeyId { get; set; } = string.Empty;
        public string Algorithm { get; set; } = "ECDSA-P256";
        public string PublicKey { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
    }
}
