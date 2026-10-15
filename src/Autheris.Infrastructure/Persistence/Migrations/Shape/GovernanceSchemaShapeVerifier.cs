using System.Data.Common;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>Result of a shape verification: the deviations from the expected shape and the tolerated extra tables.</summary>
internal sealed record SchemaShapeResult(IReadOnlyList<SchemaDeviation> Deviations, IReadOnlyList<string> ExtraTables);

/// <summary>Verifies the actual governance schema against the embedded snapshot of the baseline. Read-only (plan D-1).</summary>
internal sealed class GovernanceSchemaShapeVerifier
{
    private readonly Lazy<GovernanceSchemaShape> _expected;
    private readonly IGovernanceSchemaCatalogReader _reader;

    public GovernanceSchemaShapeVerifier(string engine, IGovernanceSchemaCatalogReader reader)
        : this(() => GovernanceSchemaShapeLoader.LoadEmbedded(engine), reader)
    {
    }

    internal GovernanceSchemaShapeVerifier(Func<GovernanceSchemaShape> expected, IGovernanceSchemaCatalogReader reader)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(reader);
        _expected = new Lazy<GovernanceSchemaShape>(expected, LazyThreadSafetyMode.ExecutionAndPublication);
        _reader = reader;
    }

    /// <summary>The expected shape. Throws when the snapshot is missing or empty (fail closed).</summary>
    public GovernanceSchemaShape Expected => _expected.Value;

    public async Task<SchemaShapeResult> VerifyAsync(DbConnection connection, CancellationToken ct, DbTransaction? transaction = null)
    {
        var expected = _expected.Value;
        var actual = await _reader.ReadAsync(connection, expected.Tables.Select(t => t.Name).ToArray(), ct, transaction).ConfigureAwait(false);
        var deviations = GovernanceSchemaShapeComparer.Compare(expected, actual);

        var known = expected.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = (await _reader.ListTablesAsync(connection, ct, transaction).ConfigureAwait(false)).Where(t => !known.Contains(t)).ToArray();
        return new SchemaShapeResult(deviations, extras);
    }
}
