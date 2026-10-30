using Autheris.Application.Sql.Catalog;
using Autheris.Infrastructure.Persistence.Catalog;
using Shouldly;
using TrinoSqlEngine;

namespace Autheris.Tests.Unit.Sql;

/// <summary>I403 WP-403-1: the key and index readers (SQL goldens and registration). Container behaviour is in the integration contract tests.</summary>
public sealed class KeyIndexMetadataReaderTests
{
    private const string SqlServerGolden = """
        SELECT i.name, i.is_primary_key, CASE WHEN i.type = 1 THEN 1 ELSE 0 END, ic.key_ordinal, c.name, ic.is_descending_key
        FROM sys.indexes AS i
        JOIN sys.objects AS o ON o.object_id = i.object_id AND o.type = 'U'
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0 AND ic.is_included_column = 0
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE s.name = @schema AND o.name = @table
          AND (i.is_primary_key = 1 OR i.is_unique = 1) AND i.has_filter = 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0
        ORDER BY i.index_id, ic.key_ordinal
        """;

    private const string PostgreSqlGolden = """
        SELECT ic.relname::text, i.indisprimary, i.indisclustered, u.ord::int, a.attname::text,
               ((i.indoption[(u.ord - 1)::int] & 1) = 1)
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class k ON k.oid = i.indrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = k.relnamespace
        JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_catalog.pg_am am ON am.oid = ic.relam
        CROSS JOIN LATERAL pg_catalog.unnest(i.indkey) WITH ORDINALITY AS u(attnum, ord)
        JOIN pg_catalog.pg_attribute a ON a.attrelid = k.oid AND a.attnum = u.attnum
        WHERE n.nspname = @schema AND k.relname = @table AND am.amname = 'btree'
          AND i.indisunique AND i.indisvalid AND i.indisready AND i.indpred IS NULL AND i.indexprs IS NULL
          AND u.ord <= i.indnkeyatts
        ORDER BY i.indexrelid, u.ord
        """;

    [Fact]
    public void SqlServerReader_Sql_MatchesGolden()
    {
        SqlServerKeyIndexMetadataReader.Sql.ShouldBe(SqlServerGolden);
        new SqlServerKeyIndexMetadataReader().Dialect.ShouldBe(TargetSqlDialect.SqlServer);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-02")]
    public void PostgreSqlReader_Sql_MatchesGolden()
    {
        // I403-T-02: bound parameters only, no concatenation
        PostgreSqlKeyIndexMetadataReader.Sql.ShouldBe(PostgreSqlGolden);
        new PostgreSqlKeyIndexMetadataReader().Dialect.ShouldBe(TargetSqlDialect.PostgreSql);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-02")]
    public void Reader_Sql_NoInterpolation_BoundParametersOnly_BothDialects()
    {
        // I403-T-S01: the only variable parts are @schema and @table; the SQL text is a compile-time constant
        foreach (var sql in new[] { SqlServerKeyIndexMetadataReader.Sql, PostgreSqlKeyIndexMetadataReader.Sql })
        {
            sql.ShouldContain("@schema");
            sql.ShouldContain("@table");
            sql.ShouldNotContain("{");
            sql.ShouldNotContain("+ ");
            sql.ShouldNotContain("'@");
            sql.ShouldNotContain("EXEC");
            sql.ShouldNotContain("format(", Case.Insensitive);
        }
    }

    [Fact]
    public void Readers_RegisteredOnlyForSqlServerAndPostgreSql()
    {
        // I403-T-03: no reader exists for SQLite, Databricks, Oracle or DuckDB, so their direction stays unknown (ascending)
        var dialects = typeof(SqlServerKeyIndexMetadataReader).Assembly
            .GetTypes().Where(t => typeof(IKeyIndexMetadataReader).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => ((IKeyIndexMetadataReader)Activator.CreateInstance(t)!).Dialect)
            .OrderBy(d => d).ToArray();
        dialects.ShouldBe(new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql }.OrderBy(d => d).ToArray());
    }
}
