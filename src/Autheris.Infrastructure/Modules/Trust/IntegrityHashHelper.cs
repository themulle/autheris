namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Shared low-level SHA-256 hashing, constant-time comparison, and manifest JSON parsing helpers.
/// Trust roots and authorization decisions remain strictly separated between customer plugins and signed modules.
/// </summary>
public static class IntegrityHashHelper
{
    public static byte[] ComputeSha256Bytes(ReadOnlySpan<byte> content) =>
        SHA256.HashData(content);

    public static string ComputeSha256HexLower(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static byte[] ComputeFileSha256Bytes(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return SHA256.HashData(stream);
    }

    public static string ComputeFileSha256HexLower(string filePath) =>
        Convert.ToHexStringLower(ComputeFileSha256Bytes(filePath));

    public static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isDigit = c is >= '0' and <= '9';
            bool isLowerHex = c is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHex)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isDigit = c is >= '0' and <= '9';
            bool isLowerHex = c is >= 'a' and <= 'f';
            bool isUpperHex = c is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    public static bool FixedTimeEqualsHex(string expectedHex, ReadOnlySpan<byte> actualHashBytes)
    {
        if (!IsHexSha256(expectedHex) || actualHashBytes.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualHashBytes);
    }

    /// <summary>
    /// Returns true if <paramref name="dllFilePath"/> resides inside a directory (up to <paramref name="rootDirectory"/>)
    /// that contains a <c>module.json</c> file, indicating a module package rather than a customer plugin.
    /// </summary>
    public static bool ContainsModuleManifestInHierarchy(string rootDirectory, string dllFilePath)
    {
        var fullRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentDir = Path.GetDirectoryName(Path.GetFullPath(dllFilePath));

        while (!string.IsNullOrEmpty(currentDir))
        {
            if (File.Exists(Path.Combine(currentDir, "module.json")))
            {
                return true;
            }

            var trimmed = currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmed, fullRoot, StringComparison.Ordinal))
            {
                break;
            }

            if (!trimmed.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                break;
            }

            currentDir = Path.GetDirectoryName(trimmed);
        }

        return false;
    }

    /// <summary>
    /// Parses a customer plugin manifest JSON document (<c>{"files":{...}}</c>, <c>{"plugins":[...]}</c>, <c>{"plugins":{...}}</c>, or flat map).
    /// </summary>
    public static Dictionary<string, string> ParsePluginManifestJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (doc.RootElement.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in filesElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    result[prop.Name] = prop.Value.GetString()!;
                }
            }

            return result;
        }

        if (doc.RootElement.TryGetProperty("plugins", out var pluginsElement))
        {
            if (pluginsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pluginsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("file", out var f) &&
                        item.TryGetProperty("sha256", out var s))
                    {
                        result[f.GetString()!] = s.GetString()!;
                    }
                }

                return result;
            }

            if (pluginsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pluginsElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString()!;
                }

                return result;
            }
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetString()!;
        }

        return result;
    }
}
