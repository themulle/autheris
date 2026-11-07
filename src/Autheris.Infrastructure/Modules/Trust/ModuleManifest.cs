namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

/// <summary>
/// Represents a single file entry in a signed <c>module.json</c> manifest.
/// </summary>
public sealed record ModuleManifestFile(string Path, string Sha256);

/// <summary>
/// Signed manifest describing an external Autheris module package (<c>module.json</c>).
/// Parsing is fail-closed: unknown properties and duplicate JSON property names are rejected.
/// </summary>
public sealed record ModuleManifest(
    int Schema,
    string Id,
    string Version,
    string HostVersion,
    string AbstractionsVersion,
    string EntryAssembly,
    string EntryType,
    IReadOnlyList<ModuleManifestFile> Files,
    string KeyId,
    string Signature)
{
    private static readonly HashSet<string> AllowedTopLevelKeys = new(StringComparer.Ordinal)
    {
        "schema",
        "id",
        "version",
        "hostVersion",
        "abstractionsVersion",
        "entryAssembly",
        "entryType",
        "files",
        "keyId",
        "signature",
    };

    private static readonly HashSet<string> AllowedFileKeys = new(StringComparer.Ordinal)
    {
        "path",
        "sha256",
    };

    public static ModuleManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    public static ModuleManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw new FormatException("Module manifest JSON cannot be empty.");
        }

        // Strip UTF-8 BOM if present? No: canonical UTF-8 JSON must not contain a BOM, and strict parsing rejects invalid tokens.
        var readerOptions = new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        };

        var reader = new Utf8JsonReader(utf8Json, readerOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new FormatException("Module manifest root must be a JSON object.");
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        int? schema = null;
        string? id = null;
        string? version = null;
        string? hostVersion = null;
        string? abstractionsVersion = null;
        string? entryAssembly = null;
        string? entryType = null;
        List<ModuleManifestFile>? files = null;
        string? keyId = null;
        string? signature = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new FormatException("Expected JSON property name in module manifest.");
            }

            string propName = reader.GetString() ?? throw new FormatException("Null property name in module manifest.");
            if (!AllowedTopLevelKeys.Contains(propName))
            {
                throw new FormatException($"Unknown field '{propName}' in module manifest.");
            }

            if (!seenKeys.Add(propName))
            {
                throw new FormatException($"Duplicate JSON property '{propName}' in module manifest.");
            }

            if (!reader.Read())
            {
                throw new FormatException($"Unexpected end of JSON after property '{propName}'.");
            }

            switch (propName)
            {
                case "schema":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int s))
                    {
                        throw new FormatException("Manifest 'schema' must be an integer.");
                    }
                    schema = s;
                    break;

                case "id":
                    id = ReadRequiredString(ref reader, propName);
                    break;

                case "version":
                    version = ReadRequiredString(ref reader, propName);
                    break;

                case "hostVersion":
                    hostVersion = ReadRequiredString(ref reader, propName);
                    break;

                case "abstractionsVersion":
                    abstractionsVersion = ReadRequiredString(ref reader, propName);
                    break;

                case "entryAssembly":
                    entryAssembly = ReadRequiredString(ref reader, propName);
                    break;

                case "entryType":
                    entryType = ReadRequiredString(ref reader, propName);
                    break;

                case "keyId":
                    keyId = ReadRequiredString(ref reader, propName);
                    break;

                case "signature":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new FormatException("Manifest 'signature' must be a string.");
                    }
                    signature = reader.GetString() ?? string.Empty;
                    break;

                case "files":
                    files = ReadFilesArray(ref reader);
                    break;
            }
        }

        if (reader.Read())
        {
            throw new FormatException("Trailing content after module manifest root object is not allowed.");
        }

        if (schema != 1)
        {
            throw new FormatException($"Unsupported or missing module manifest schema version '{schema?.ToString() ?? "null"}'; expected 1.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new FormatException("Manifest 'id' is required.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new FormatException("Manifest 'version' is required.");
        }

        if (string.IsNullOrWhiteSpace(hostVersion))
        {
            throw new FormatException("Manifest 'hostVersion' is required.");
        }

        if (string.IsNullOrWhiteSpace(abstractionsVersion))
        {
            throw new FormatException("Manifest 'abstractionsVersion' is required.");
        }

        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            throw new FormatException("Manifest 'entryAssembly' is required.");
        }

        if (!IsValidRelativeManifestPath(entryAssembly))
        {
            throw new FormatException($"Manifest 'entryAssembly' path '{entryAssembly}' is invalid.");
        }

        if (string.IsNullOrWhiteSpace(entryType))
        {
            throw new FormatException("Manifest 'entryType' is required.");
        }

        if (files is null)
        {
            throw new FormatException("Manifest 'files' array is required.");
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new FormatException("Manifest 'keyId' is required.");
        }

        return new ModuleManifest(
            Schema: schema.Value,
            Id: id,
            Version: version,
            HostVersion: hostVersion,
            AbstractionsVersion: abstractionsVersion,
            EntryAssembly: entryAssembly,
            EntryType: entryType,
            Files: files,
            KeyId: keyId,
            Signature: signature ?? string.Empty);
    }

    public static bool IsValidRelativeManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal) ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            Path.IsPathRooted(path))
        {
            return false;
        }

        if (string.Equals(path, "module.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                string.Equals(segment, ".", StringComparison.Ordinal) ||
                string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadRequiredString(ref Utf8JsonReader reader, string propName)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new FormatException($"Manifest '{propName}' must be a string.");
        }

        var val = reader.GetString();
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new FormatException($"Manifest '{propName}' cannot be empty.");
        }

        return val;
    }

    private static List<ModuleManifestFile> ReadFilesArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new FormatException("Manifest 'files' must be a JSON array.");
        }

        var list = new List<ModuleManifestFile>();
        var seenFilePaths = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return list;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new FormatException("Each item in 'files' must be a JSON object.");
            }

            var seenItemKeys = new HashSet<string>(StringComparer.Ordinal);
            string? filePath = null;
            string? sha256 = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new FormatException("Expected property name in 'files' item.");
                }

                string itemKey = reader.GetString() ?? throw new FormatException("Null property name in 'files' item.");
                if (!AllowedFileKeys.Contains(itemKey))
                {
                    throw new FormatException($"Unknown field '{itemKey}' in manifest 'files' entry.");
                }

                if (!seenItemKeys.Add(itemKey))
                {
                    throw new FormatException($"Duplicate JSON property '{itemKey}' in manifest 'files' entry.");
                }

                if (!reader.Read())
                {
                    throw new FormatException($"Unexpected end of JSON in 'files' entry after '{itemKey}'.");
                }

                switch (itemKey)
                {
                    case "path":
                        filePath = ReadRequiredString(ref reader, "files[].path");
                        break;
                    case "sha256":
                        sha256 = ReadRequiredString(ref reader, "files[].sha256");
                        break;
                }
            }

            if (!IsValidRelativeManifestPath(filePath))
            {
                throw new FormatException($"Invalid manifest file path '{filePath}'. Paths must be relative with '/' separators and no '..' segments.");
            }

            if (!IntegrityHashHelper.IsLowerHexSha256(sha256))
            {
                throw new FormatException($"Invalid SHA-256 hex digest '{sha256}' for file '{filePath}'. Must be 64 lowercase hex characters.");
            }

            if (!seenFilePaths.Add(filePath!))
            {
                throw new FormatException($"Duplicate file path '{filePath}' in manifest 'files' array.");
            }

            list.Add(new ModuleManifestFile(filePath!, sha256!));
        }

        throw new FormatException("Unexpected end of JSON while reading 'files' array.");
    }
}
