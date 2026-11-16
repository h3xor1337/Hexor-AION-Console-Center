using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hexor;

// Records only Hexor-observed downloads. DPAPI protects the per-user signing key at rest;
// it detects ordinary manifest tampering but is not a substitute for publisher signatures.
public sealed record UpdaterCacheOwnershipRecord(string CanonicalPath, string Sha256, long Size, string UpdaterIdentity, DateTimeOffset RecordedUtc, string TrustedEvidence);
public sealed record UpdaterCacheOwnershipManifest(int Version, IReadOnlyList<UpdaterCacheOwnershipRecord> Records, string Signature);
public sealed class UpdaterCacheOwnershipStore
{
    private readonly string manifestPath; private readonly string keyPath;
    public UpdaterCacheOwnershipStore(string directory)
    { Directory.CreateDirectory(directory); manifestPath = Path.Combine(directory, "updater-cache-ownership.v1.json"); keyPath = Path.Combine(directory, "updater-cache-ownership.v1.key"); }
    public void RecordManagedDownload(string filePath, string updaterIdentity, string trustedEvidence)
    {
        if (string.IsNullOrWhiteSpace(updaterIdentity) || string.IsNullOrWhiteSpace(trustedEvidence) || !File.Exists(filePath)) throw new InvalidDataException("Managed-download evidence is required.");
        var info = new FileInfo(filePath); var record = new UpdaterCacheOwnershipRecord(Path.GetFullPath(filePath), Hash(filePath), info.Length, updaterIdentity, DateTimeOffset.UtcNow, trustedEvidence);
        var records = ReadVerified().Where(x => !x.CanonicalPath.Equals(record.CanonicalPath, StringComparison.OrdinalIgnoreCase)).Append(record).ToArray(); Write(records);
    }
    public IReadOnlyList<UpdaterCacheOwnershipRecord> ReadVerified()
    {
        if (!File.Exists(manifestPath)) return [];
        var manifest = JsonSerializer.Deserialize<UpdaterCacheOwnershipManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Ownership manifest is invalid.");
        if (manifest.Version != 1 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(manifest.Signature), Sign(manifest.Records))) throw new InvalidDataException("Ownership manifest integrity verification failed.");
        return manifest.Records;
    }
    private void Write(IReadOnlyList<UpdaterCacheOwnershipRecord> records)
    { var manifest = new UpdaterCacheOwnershipManifest(1, records, Convert.ToHexString(Sign(records))); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })); }
    private byte[] Sign(IReadOnlyList<UpdaterCacheOwnershipRecord> records) => HMACSHA256.HashData(GetKey(), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records)));
    private byte[] GetKey()
    { if (File.Exists(keyPath)) return ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser); var key = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(keyPath, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)); return key; }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
