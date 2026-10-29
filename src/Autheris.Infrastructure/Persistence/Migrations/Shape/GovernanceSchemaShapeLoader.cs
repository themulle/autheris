using System.Text.Json;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>
/// Loads the embedded expected shape of an engine (<c>governance-shape.&lt;engine&gt;.v0001.json</c>, the frozen v1.5.0 shape). Fail closed (SEC-94-08a):
/// a missing, empty or unparseable snapshot throws, an empty expected shape must never pass verification.
/// </summary>
internal static class GovernanceSchemaShapeLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static string ResourceName(string engine) => $"governance-shape.{engine}.v0001.json";

    /// <summary>The shape at <paramref name="version"/>: v0001 folded with the deltas of the steps 2..version (plan D-5).</summary>
    public static GovernanceSchemaShape LoadAt(string engine, int version, IReadOnlyDictionary<int, GovernanceSchemaShapeDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        var shape = LoadEmbedded(engine);
        for (var v = 2; v <= version; v++)
        {
            shape = GovernanceSchemaShapeFolder.Fold(
                shape,
                deltas.TryGetValue(v, out var delta) ? delta : throw new InvalidOperationException($"Migration {v:D4} has no shape delta (fail closed)."));
        }

        return shape;
    }

    public static GovernanceSchemaShape LoadEmbedded(string engine)
    {
        var name = ResourceName(engine);
        using var stream = typeof(GovernanceSchemaShapeLoader).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded governance schema snapshot '{name}' is missing (fail closed).");
        return Parse(stream, engine, name);
    }

    public static GovernanceSchemaShape Parse(Stream stream, string engine, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        GovernanceSchemaShape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<GovernanceSchemaShape>(stream, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The embedded governance schema snapshot '{resourceName}' cannot be parsed (fail closed).", ex);
        }

        if (shape is null || shape.Tables is null || shape.Tables.Count == 0 || shape.Tables.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) || t.Columns is null || t.Columns.Count == 0))
        {
            throw new InvalidOperationException($"The embedded governance schema snapshot '{resourceName}' is empty or incomplete (fail closed).");
        }

        if (!string.Equals(shape.Engine, engine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The embedded governance schema snapshot '{resourceName}' belongs to engine '{shape.Engine}', not '{engine}' (fail closed).");
        }

        return shape with
        {
            Tables = shape.Tables.Select(t => t with
            {
                PrimaryKey = t.PrimaryKey ?? [],
                Indexes = (t.Indexes ?? []).Select(i => i with { Columns = i.Columns ?? [] }).ToArray()
            }).ToArray()
        };
    }

    public static string Serialize(GovernanceSchemaShape shape) => JsonSerializer.Serialize(shape, Json).ReplaceLineEndings("\n") + "\n";
}
