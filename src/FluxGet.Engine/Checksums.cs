using System.Security.Cryptography;

namespace FluxGet.Engine;

public static class Checksums
{
    public static (string Algorithm, string Hex)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        string algo;
        var colon = v.IndexOf(':');
        if (colon > 0)
        {
            algo = v[..colon].Trim().ToLowerInvariant().Replace("-", "");
            v = v[(colon + 1)..].Trim();
        }
        else
        {
            algo = v.Length switch { 32 => "md5", 40 => "sha1", 64 => "sha256", 128 => "sha512", _ => "" };
        }
        v = v.ToLowerInvariant();
        var expected = algo switch { "md5" => 32, "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => -1 };
        if (expected < 0 || v.Length != expected || !v.All(Uri.IsHexDigit))
            throw new ArgumentException("Checksum must be MD5, SHA-1, SHA-256 or SHA-512 in hex, optionally prefixed like 'sha256:'.");
        return (algo, v);
    }

    public static async Task<string> ComputeAsync(string path, string algorithm, Action<long>? progress, CancellationToken ct)
    {
        using var hash = algorithm switch
        {
            "md5" => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            "sha1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            "sha512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        };
        var buffer = new byte[1024 * 1024];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long total = 0;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            total += n;
            progress?.Invoke(total);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
