namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// Persisted shape of a validated semantic manifest (#112 C-4). <see cref="FormatVersion"/> versions the JSON shape stored in the
/// <c>manifest_json</c> column; a row with another format is treated as "no manifest" (fail closed), never as a startup failure.
/// </summary>
public static class SemanticManifestJson
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 32
    };

    public static string Serialize(DbtSemanticManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    /// <summary>Reads the stored shape. Returns <c>null</c> for anything that is not a manifest of this format.</summary>
    public static DbtSemanticManifest? TryDeserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<DbtSemanticManifest>(json, Options);
            if (manifest is null || manifest.SemanticModels is null || manifest.Metrics is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return null;
            }

            return manifest with
            {
                SemanticModels = manifest.SemanticModels
                    .Select(static m => m with { Entities = m.Entities ?? [], Measures = m.Measures ?? [], Dimensions = m.Dimensions ?? [] })
                    .ToList(),
                Metrics = manifest.Metrics
                    .Select(static m => m with
                    {
                        Meta = new Dictionary<string, string>(m.Meta ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                        Dimensions = m.Dimensions ?? [],
                        Measures = m.Measures ?? []
                    })
                    .ToList()
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 hex (lower case) of the UTF-8 bytes of <paramref name="json"/>.</summary>
    public static string ComputeHash(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
