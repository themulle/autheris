namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Produces the deterministic canonical UTF-8 JSON representation of a <see cref="ModuleManifest"/>
/// used for ECDSA P-256 signature generation and verification.
/// </summary>
public static class ManifestCanonicalizer
{
    /// <summary>
    /// Serializes <paramref name="manifest"/> to canonical UTF-8 bytes:
    /// fixed <c>"schema":1</c> prefix field, remaining keys sorted ordinally at every level,
    /// no insignificant whitespace, <c>files</c> sorted by <c>path</c> (ordinal),
    /// paths relative with <c>/</c> separators, lowercase hex SHA-256, and <c>signature</c> excluded.
    /// </summary>
    public static byte[] ToCanonicalBytes(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Schema != 1)
        {
            throw new FormatException($"Unsupported module manifest schema '{manifest.Schema}'; expected 1.");
        }

        if (!ModuleManifest.IsValidRelativeManifestPath(manifest.EntryAssembly))
        {
            throw new FormatException($"Invalid entryAssembly path '{manifest.EntryAssembly}'.");
        }

        var sortedFiles = (manifest.Files ?? [])
            .Select(f =>
            {
                var normalizedPath = f.Path.Replace('\\', '/');
                if (!ModuleManifest.IsValidRelativeManifestPath(normalizedPath))
                {
                    throw new FormatException($"Invalid file path '{f.Path}' in manifest.");
                }

                var lowerSha = f.Sha256.ToLowerInvariant();
                if (!IntegrityHashHelper.IsLowerHexSha256(lowerSha))
                {
                    throw new FormatException($"Invalid SHA-256 digest '{f.Sha256}' for file '{f.Path}'.");
                }

                return new ModuleManifestFile(normalizedPath, lowerSha);
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToArray();

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            // Fixed prefix field
            writer.WriteNumber("schema", 1);

            // Remaining top-level fields sorted ordinally (excluding "schema" and "signature"):
            // abstractionsVersion, entryAssembly, entryType, files, hostVersion, id, keyId, version
            writer.WriteString("abstractionsVersion", manifest.AbstractionsVersion);
            writer.WriteString("entryAssembly", manifest.EntryAssembly.Replace('\\', '/'));
            writer.WriteString("entryType", manifest.EntryType);

            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var file in sortedFiles)
            {
                writer.WriteStartObject();
                // Sorted ordinally: "path", "sha256"
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteString("hostVersion", manifest.HostVersion);
            writer.WriteString("id", manifest.Id);
            writer.WriteString("keyId", manifest.KeyId);
            writer.WriteString("version", manifest.Version);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses a raw UTF-8 JSON manifest and returns its canonical UTF-8 byte representation.
    /// </summary>
    public static byte[] ToCanonicalBytes(ReadOnlySpan<byte> rawManifestUtf8)
    {
        var manifest = ModuleManifest.Parse(rawManifestUtf8);
        return ToCanonicalBytes(manifest);
    }

    /// <summary>
    /// Parses a raw JSON manifest string and returns its canonical UTF-8 byte representation.
    /// </summary>
    public static byte[] ToCanonicalBytes(string rawManifestJson)
    {
        var manifest = ModuleManifest.Parse(rawManifestJson);
        return ToCanonicalBytes(manifest);
    }
}
