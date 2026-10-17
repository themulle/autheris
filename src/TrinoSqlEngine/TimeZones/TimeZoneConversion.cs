namespace TrinoSqlEngine;

using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Proof that <see cref="TimeZoneConversion.ValidateFederated"/> ran over a federated statement. Only that method creates one, and
/// <see cref="TimeZoneConversion.GenerateFederated"/> needs it, so the DuckDB generator cannot be asked to emit an unchecked <c>convert_timezone</c>
/// from the planner (BC-130-4, BC-130-12).
/// </summary>
public sealed class TimeZoneValidationProof
{
    internal TimeZoneValidationProof()
    {
    }
}

/// <summary>Public facade of the time zone call validator for the federated planner, which builds DuckDB SQL without the governed compiler.</summary>
public static class TimeZoneConversion
{
    private static readonly InMemoryTableCatalog NoCatalog = new(Array.Empty<TableCatalogEntry>(), "main");

    /// <summary>
    /// Checks every call of <c>convert_timezone</c> of a user statement that is about to run on the staged DuckDB. Stricter than the governed path
    /// (A-8): a bare column is not a timestamp (the catalog types of a federated statement belong to the source dialects; the caller writes
    /// <c>CAST(col AS timestamp)</c>), and a parameter is not a zone until the federated binding exists. Masked columns are never accepted.
    /// </summary>
    /// <param name="isMaskedByName">Whether a (qualifier, column) pair names a masked or denied column of one of the staged tables; null when none is.</param>
    /// <exception cref="SqlTimeZoneException">The statement contains a call that is not valid for the federated DuckDB path.</exception>
    public static TimeZoneValidationProof ValidateFederated(SqlStatement statement, bool duckDbAvailable, Func<string?, string, bool>? isMaskedByName = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb);
        new TimeZoneCallValidator(NoCatalog, new TimeZoneValidationOptions(
            TargetSqlDialect.DuckDb, caps.SupportsTimeZoneConversion, duckDbAvailable, NoMasks.Instance, System.Collections.Frozen.FrozenSet<string>.Empty,
            ClientListShapes.Empty, Federated: true, isMaskedByName)).Validate(statement);
        return new TimeZoneValidationProof();
    }

    /// <summary>Generates DuckDB SQL for a federated statement that was validated; every query value is bound (<c>$n</c>).</summary>
    public static CompiledSql GenerateFederated(SqlStatement statement, TimeZoneValidationProof proof)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(proof);
        var generator = (SqlDialectGeneratorBase)SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.DuckDb);
        return generator.Generate(statement, ParameterSource.Empty, DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb), CancellationToken.None, timeZoneCallsValidated: true);
    }

    private sealed class NoMasks : IColumnMaskProvider
    {
        public static NoMasks Instance { get; } = new();

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException("No masks.");
    }
}
