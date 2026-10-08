namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TrinoSqlEngine;

/// <summary>
/// Virtual filters, phase 7: stage 2 definitions, a correlated predicate in Trino syntax
/// (<c>from ... [join ...] where ...</c>) that addresses the protected object as <c>target</c> (design 3.7).
/// <see cref="Validate"/> runs before a definition is stored: one read-only statement fragment, <c>target</c> reserved
/// (never a table or alias), only catalogued tables of the filter's data source, functions from the filter list (Trino
/// names, checked before translation; <c>date_diff</c> is not allowed, decision 8), no parameters, and translatable into
/// the data source's dialect. <see cref="Compile"/> renders it per dialect with the Trino date functions translated,
/// correlated with the reserved alias <c>autheris_target</c>. Time functions stay functions: no value is ever baked in.
/// </summary>
public static partial class SqlFilterCompiler
{
    private const string TargetAlias = "target";

    /// <summary>Functions a filter may call (Trino names); everything else is rejected before translation.</summary>
    public static readonly FrozenSet<string> AllowedFunctions = new[]
    {
        "current_timestamp", "current_date", "date_add", "date_trunc", "now", "coalesce", "nullif", "lower", "upper", "trim", "length", "substring",
        "abs", "round", "floor", "ceil", "ceiling"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^\s*from\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithFrom();

    /// <summary>Validates <paramref name="filter"/>'s SQL against <paramref name="catalog"/> and returns it with the derived target columns.</summary>
    /// <exception cref="ArgumentException">The definition is not acceptable (the message says why).</exception>
    public static VirtualFilter Validate(VirtualFilter filter, IReadOnlyList<TableMetadata> catalog)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(catalog);
        filter.Validate();
        var fragment = filter.Sql ?? throw new ArgumentException($"The virtual filter '{filter.Name}' has no sql definition.", nameof(filter));

        if (!StartsWithFrom().IsMatch(fragment))
        {
            throw Reject(filter, "the definition must start with FROM (the predicate form: from ... where ... target.<column> ...).");
        }

        var withoutLiterals = StringLiteral().Replace(fragment, "''");
        if (withoutLiterals.Contains('?') || withoutLiterals.Contains('@') || withoutLiterals.Contains(':'))
        {
            throw Reject(filter, "parameters (?, @name, :name) are not allowed.");
        }

        if (StringLiteral().Matches(fragment).Any(m => m.Value.Contains(TargetAlias, StringComparison.OrdinalIgnoreCase)))
        {
            throw Reject(filter, "string literals must not contain the reserved name 'target'.");
        }

        TrinoSqlEngine.Analysis.SqlQueryMetadata metadata;
        try
        {
            metadata = new FastSqlEngine().Analyze(Statement(fragment).AsMemory());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Reject(filter, $"the definition must be one read-only statement fragment ({ex.Message}).");
        }

        if (metadata.StatementType != TrinoSqlEngine.Analysis.SqlStatementType.Select)
        {
            throw Reject(filter, "only a read-only statement fragment is allowed.");
        }

        var sourceTables = new List<TableMetadata>();
        foreach (var table in metadata.ReferencedTables)
        {
            if (string.Equals(table.Alias, TargetAlias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(table.TableName, TargetAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, "'target' is reserved for the protected object and cannot be a table or alias.");
            }

            if (!string.IsNullOrWhiteSpace(table.Catalog) && !string.Equals(table.Catalog, filter.Source, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, $"the table '{table.FullName}' is not in the filter's data source '{filter.Source}'.");
            }

            if (string.IsNullOrWhiteSpace(table.Schema))
            {
                throw Reject(filter, $"the table '{table.FullName}' must be written as schema.table.");
            }

            var catalogued = catalog.FirstOrDefault(t =>
                string.Equals(t.Identifier.Domain, filter.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.Schema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.TableName, table.TableName, StringComparison.OrdinalIgnoreCase));
            sourceTables.Add(catalogued ?? throw Reject(filter, $"the table '{table.FullName}' is unknown in the catalog of '{filter.Source}'."));
        }

        foreach (var function in metadata.FunctionCalls ?? [])
        {
            var name = function.Split('.')[^1];
            if (string.Equals(name, "date_diff", StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(filter, "date_diff is not allowed (its meaning differs between Trino and SQL Server, decision 8).");
            }

            if (!AllowedFunctions.Contains(name))
            {
                throw Reject(filter, $"the function '{name}' is not allowed in virtual filters.");
            }
        }

        var targetColumns = (metadata.FilterColumnReferences ?? [])
            .Select(r => (r.TableOrAlias, r.ColumnName))
            .Concat((metadata.JoinColumnReferences ?? []).Select(r => (r.TableOrAlias, r.ColumnName)))
            .Where(r => string.Equals(r.TableOrAlias, TargetAlias, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ColumnName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetColumns.Count == 0)
        {
            throw Reject(filter, "the predicate must reference the protected object as target.<column>.");
        }

        foreach (var column in targetColumns)
        {
            VirtualFilterNames.ValidateIdentifier(column, nameof(filter));
        }

        var validated = filter with { SqlTargetColumns = targetColumns };
        var dialect = sourceTables.Count > 0 ? sourceTables[0].Dialect : DatabaseDialect.SqlServer;
        try
        {
            Compile(validated, dialect);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArgumentException)
        {
            throw Reject(filter, $"it cannot be translated for {dialect}: {ex.Message}");
        }

        return validated;
    }

    /// <summary>EXISTS predicate of the SQL definition in <paramref name="dialect"/>, correlated with <c>autheris_target</c>.</summary>
    public static string Compile(VirtualFilter filter, DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var fragment = filter.Sql ?? throw new InvalidOperationException($"The virtual filter '{filter.Name}' has no sql definition.");
        var options = new RlsOptions
        {
            TargetDialect = dialect switch
            {
                DatabaseDialect.SqlServer => TargetSqlDialect.SqlServer,
                DatabaseDialect.PostgreSql => TargetSqlDialect.PostgreSql,
                DatabaseDialect.Sqlite => TargetSqlDialect.Sqlite,
                DatabaseDialect.Oracle => TargetSqlDialect.Oracle,
                _ => throw new NotSupportedException($"Virtual filters with a sql definition are not supported for {dialect}.")
            },
            RewriterEngine = "AstCompiler",
            TranslateTrinoDateFunctions = true,
            PolicyProvider = new DefaultRlsPolicyProvider(predicate: _ => false),
            EnforceReadOnlyQueries = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = AllowedFunctions,
            EnforcedMaxRows = 0
        };

        var generated = new FastSqlEngine().GenerateGovernedSql(Statement(fragment), options);
        if (!generated.StartsWith("SELECT 1 FROM ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The virtual filter '{filter.Name}' did not compile to a correlated subquery.");
        }

        var quotedTarget = dialect.QuoteIdentifier(TargetAlias) + ".";
        var correlated = generated.Replace(quotedTarget, dialect.QuoteIdentifier(RowFilterAliases.Target) + ".", StringComparison.Ordinal);
        return $"EXISTS ({correlated})";
    }

    private static string Statement(string fragment) => "SELECT 1 " + fragment.Trim();

    private static ArgumentException Reject(VirtualFilter filter, string reason) =>
        new($"The sql definition of the virtual filter '{filter.Name}' is rejected: {reason}", nameof(filter));
}
