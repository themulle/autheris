using System.Security;
using System.Security.Cryptography;

namespace Autheris.Infrastructure.Plugins;

/// <summary>
/// SEC M-27: Allow-list of SHA-256 hashes for every file (plugin, managed dependency, native library) that may be
/// loaded from a plugin directory. Keys are either the path relative to the plugin directory (forward slashes) or
/// the bare file name.
/// </summary>
public sealed class PluginTrustList
{
    private readonly string _rootDirectory;
    private readonly Dictionary<string, string> _hashes;

    public PluginTrustList(string rootDirectory, IReadOnlyDictionary<string, string> hashes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(hashes);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in hashes)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                _hashes[NormalizeKey(key)] = value.Trim();
            }
        }
    }

    public bool TryGetExpectedHash(string fullPath, out string expectedHash)
    {
        var normalizedFull = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(_rootDirectory, normalizedFull);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            expectedHash = string.Empty;
            return false;
        }

        if (_hashes.TryGetValue(NormalizeKey(relative), out var byRelative))
        {
            expectedHash = byRelative;
            return true;
        }

        if (_hashes.TryGetValue(Path.GetFileName(normalizedFull), out var byName))
        {
            expectedHash = byName;
            return true;
        }

        expectedHash = string.Empty;
        return false;
    }

    /// <summary>Reads the file once, verifies its hash and returns exactly the verified bytes.</summary>
    public byte[] ReadVerifiedBytes(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (!TryGetExpectedHash(fullPath, out var expectedHash))
        {
            throw new SecurityException($"Sicherheitsfehler: Plugin-Datei '{fileName}' ist nicht in Plugins:TrustedPluginHashes verzeichnet.");
        }

        var bytes = File.ReadAllBytes(fullPath);
        VerifyBytes(fileName, bytes, expectedHash);
        return bytes;
    }

    /// <summary>Verifies a file without keeping its bytes (native libraries are loaded by path).</summary>
    public void VerifyFile(string fullPath) => ReadVerifiedBytes(fullPath);

    /// <summary>A manifest may only repeat what the configuration says; any deviation aborts plugin loading.</summary>
    public void EnsureManifestConsistent(IReadOnlyDictionary<string, string> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var (file, manifestHash) in manifest)
        {
            var key = NormalizeKey(file);
            string? configured;
            if (!_hashes.TryGetValue(key, out configured) && !_hashes.TryGetValue(Path.GetFileName(key), out configured))
            {
                throw new SecurityException($"Sicherheitsfehler: manifest.json listet '{file}', das nicht in Plugins:TrustedPluginHashes konfiguriert ist.");
            }

            if (!HashesEqual(configured, manifestHash))
            {
                throw new SecurityException($"Sicherheitsfehler: manifest.json widerspricht der konfigurierten Integritätsangabe für '{file}'.");
            }
        }
    }

    internal static void VerifyBytes(string fileName, byte[] bytes, string expectedHash)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!HashesEqual(expectedHash, actualHash))
        {
            throw new SecurityException($"Sicherheitsfehler: Integritätsprüfung fehlgeschlagen für Plugin '{fileName}'. Erwarteter SHA-256: {expectedHash}, Tatsächlich: {actualHash}");
        }
    }

    private static bool HashesEqual(string? expectedHex, string? actualHex)
    {
        byte[] expected;
        byte[] actual;
        try
        {
            expected = Convert.FromHexString((expectedHex ?? string.Empty).Trim());
            actual = Convert.FromHexString((actualHex ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == SHA256.HashSizeInBytes && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string NormalizeKey(string key) => key.Replace('\\', '/').TrimStart('.', '/');
}
