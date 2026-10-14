namespace Autheris.Application.Policy.Lint;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Application.Sql;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>
/// The one production configuration of the policy predicate parser (SEC-X1-16): the per-dialect function allowlist of the secured
/// table's own data source, the catalog columns with their types, the catalog for correlated subqueries and the target dialect.
/// The lint and the enforcement path build their <see cref="PolicyParseContext"/> here, so the lint cannot pass while production denies.
/// </summary>
public static class PolicyParserSetup
{
    public static IPolicyExpressionParser CreateParser() => new PolicyExpressionParser(new FastSqlEngine());

    /// <summary>Catalog of every table, with column types, for correlated subqueries and column resolution.</summary>
    public static ITableCatalog CreateCatalog(IEnumerable<TableMetadata> tables, string defaultSchema = "main") =>
        new InMemoryTableCatalog(tables.Select(ToCatalogEntry), defaultSchema);

    public static TableCatalogEntry ToCatalogEntry(TableMetadata table) => new(
        new TableIdentity(table.Table.SchemaName, table.Table.TableName),
        table.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
        TenantColumn: null);

    /// <summary>The parse context of <paramref name="table"/>.</summary>
    /// <returns>False when the table's dialect is unknown or has no target dialect (the table is denied).</returns>
    public static bool TryCreateContext(
        TableMetadata table,
        ITableCatalog catalog,
        IEnumerable<string>? additionalAllowedFunctions,
        long catalogVersion,
        out PolicyParseContext context)
    {
        context = null!;
        if (!DatabaseDialectExtensions.TryParseDialect(table.Table.SourceType, out var dialect)
            || !SqlDialectMapper.TryToTargetDialect(dialect, out var target))
        {
            return false;
        }

        context = new PolicyParseContext(
            new TableIdentity(table.Table.SchemaName, table.Table.TableName),
            table.Columns.Select(c => c.ColumnName).ToList(),
            SqlFunctionAllowlists.Build(target, additionalAllowedFunctions),
            new Dictionary<string, PolicyValue>(),
            catalogVersion)
        {
            Catalog = catalog,
            TargetDialect = target
        };
        return true;
    }
}
