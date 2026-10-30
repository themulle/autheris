namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Catalog;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// I403 WP-403-1: the key and index readers against real SQL Server and PostgreSQL (Testcontainers). The fixtures throw when
/// no container runtime is reachable, so these cases cannot pass by returning early.
/// </summary>
public abstract class KeyIndexMetadataReaderContractTests
{
    protected abstract IKeyIndexMetadataReader Reader { get; }
    protected abstract string Schema { get; }
    protected abstract Task<DbConnection> OpenAsync();

    /// <summary>Quotes an identifier for the dialect (test DDL only; never used by the reader).</summary>
    protected abstract string Quote(string identifier);

    protected abstract string DescKeyword { get; }

    private static string Unique(string stem) => stem + Guid.NewGuid().ToString("N")[..8];

    private async Task<List<DbKeyIndexColumn>> ReadAsync(DbConnection conn, string table)
    {
        var list = new List<DbKeyIndexColumn>();
        await foreach (var c in Reader.ReadAsync(conn, new TableIdentity(Schema, table), CancellationToken.None))
        {
            list.Add(c);
        }

        return list;
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PrimaryKey_AscDesc_ReportsDirectionsInKeyOrder()
    {
        // I403-T-04
        await using var conn = await OpenAsync();
        var t = Unique("kd_pk");
        await ExecAsync(conn, $"CREATE TABLE {Quote(Schema)}.{Quote(t)} (a int NOT NULL, b int NOT NULL, c int NOT NULL, CONSTRAINT {Quote("pk_" + t)} PRIMARY KEY (b {DescKeyword}, a))");

        var rows = await ReadAsync(conn, t);

        rows.Select(r => (r.KeyOrdinal, r.ColumnName, r.IsDescending, r.IsPrimaryKey)).ToArray()
            .ShouldBe(new[] { (1, "b", true, true), (2, "a", false, true) });
        rows.Select(r => r.IndexName).Distinct().Single().ShouldBe("pk_" + t);
    }

    [Fact]
    public async Task IncludedColumns_FilteredAndDisabledIndexes_Excluded()
    {
        // I403-T-05
        await using var conn = await OpenAsync();
        var t = Unique("kd_inc");
        await ExecAsync(conn, $"CREATE TABLE {Quote(Schema)}.{Quote(t)} (a int NOT NULL, b int NOT NULL, c int NOT NULL, d int NULL, CONSTRAINT {Quote("pk_" + t)} PRIMARY KEY (a))");
        await ExecAsync(conn, $"CREATE UNIQUE INDEX {Quote("ux_inc_" + t)} ON {Quote(Schema)}.{Quote(t)} (b {DescKeyword}) INCLUDE (c)");
        await ExecAsync(conn, $"CREATE UNIQUE INDEX {Quote("ux_flt_" + t)} ON {Quote(Schema)}.{Quote(t)} (c) WHERE c > 0");
        await ExecAsync(conn, $"CREATE INDEX {Quote("ix_plain_" + t)} ON {Quote(Schema)}.{Quote(t)} (d)");
        await ExtraIgnoredIndexesAsync(conn, t);

        var rows = await ReadAsync(conn, t);

        rows.GroupBy(r => r.IndexName).Select(g => (g.Key, Cols: string.Join(",", g.Select(x => x.ColumnName))))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
            .ShouldBe(new[] { ("pk_" + t, "a"), ("ux_inc_" + t, "b") });
        rows.Single(r => r.IndexName == "ux_inc_" + t).IsDescending.ShouldBeTrue();
    }

    /// <summary>A disabled index (SQL Server) or an expression index (PostgreSQL) that must not be reported.</summary>
    protected abstract Task ExtraIgnoredIndexesAsync(DbConnection conn, string table);

    [Fact]
    public async Task ClusteredUniqueWithoutPrimaryKey_ReportedClustered()
    {
        // I403-T-06
        await using var conn = await OpenAsync();
        var t = Unique("kd_cl");
        await ExecAsync(conn, $"CREATE TABLE {Quote(Schema)}.{Quote(t)} (a int NOT NULL, b int NOT NULL)");
        await CreateClusteredUniqueAsync(conn, t);

        var rows = await ReadAsync(conn, t);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => !r.IsPrimaryKey && r.IsClustered);
        rows.Select(r => (r.ColumnName, r.IsDescending)).ToArray().ShouldBe(new[] { ("a", true), ("b", false) });
    }

    protected abstract Task CreateClusteredUniqueAsync(DbConnection conn, string table);

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-02")]
    public async Task QuotedMixedCaseNames_BoundNotInterpolated()
    {
        // I403-T-08 / I403-T-S01 (container half)
        await using var conn = await OpenAsync();
        var t = Unique("Kd\"Mixed'--");
        await ExecAsync(conn, $"CREATE TABLE {Quote(Schema)}.{Quote(t)} ({Quote("Id")} int NOT NULL, CONSTRAINT {Quote("pk_" + t)} PRIMARY KEY ({Quote("Id")} {DescKeyword}))");

        var rows = await ReadAsync(conn, t);

        rows.Select(r => (r.ColumnName, r.IsDescending)).ToArray().ShouldBe(new[] { ("Id", true) });
        (await ReadAsync(conn, t + "' OR '1'='1")).ShouldBeEmpty();
        (await ReadAsync(conn, t.ToUpperInvariant() + "x")).ShouldBeEmpty();
    }
}

public sealed class SqlServerKeyIndexMetadataReaderContractTests : KeyIndexMetadataReaderContractTests, IClassFixture<AstCompilerSqlServerFixture>
{
    private readonly AstCompilerSqlServerFixture _fx;

    public SqlServerKeyIndexMetadataReaderContractTests(AstCompilerSqlServerFixture fixture) => _fx = fixture;

    protected override IKeyIndexMetadataReader Reader { get; } = new SqlServerKeyIndexMetadataReader();
    protected override string Schema => "dbo";
    protected override string DescKeyword => "DESC";
    protected override string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    protected override async Task<DbConnection> OpenAsync()
    {
        _fx.Db.ShouldNotBeNull();
        _fx.Db.IsAvailable.ShouldBeTrue("SQL Server container is required for I403 evidence");
        var conn = new SqlConnection(_fx.Db.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    protected override async Task ExtraIgnoredIndexesAsync(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE UNIQUE INDEX {Quote("ux_off_" + table)} ON {Quote(Schema)}.{Quote(table)} (d); " +
                          $"ALTER INDEX {Quote("ux_off_" + table)} ON {Quote(Schema)}.{Quote(table)} DISABLE;";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task CreateClusteredUniqueAsync(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE UNIQUE CLUSTERED INDEX {Quote("cx_" + table)} ON {Quote(Schema)}.{Quote(table)} (a DESC, b)";
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class PostgreSqlKeyIndexMetadataReaderContractTests : KeyIndexMetadataReaderContractTests, IClassFixture<AstCompilerPostgreSqlFixture>
{
    private readonly AstCompilerPostgreSqlFixture _fx;

    public PostgreSqlKeyIndexMetadataReaderContractTests(AstCompilerPostgreSqlFixture fixture) => _fx = fixture;

    protected override IKeyIndexMetadataReader Reader { get; } = new PostgreSqlKeyIndexMetadataReader();
    protected override string Schema => "public";
    protected override string DescKeyword => "DESC";
    protected override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    protected override async Task<DbConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    protected override async Task ExtraIgnoredIndexesAsync(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE UNIQUE INDEX {Quote("ux_expr_" + table)} ON {Quote(Schema)}.{Quote(table)} ((d + 1))";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task CreateClusteredUniqueAsync(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE UNIQUE INDEX {Quote("cx_" + table)} ON {Quote(Schema)}.{Quote(table)} (a DESC, b); " +
                          $"CLUSTER {Quote(Schema)}.{Quote(table)} USING {Quote("cx_" + table)};";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PostgreSql_NonBtreeAndExpressionIndexes_Excluded()
    {
        // I403-T-07
        await using var conn = await OpenAsync();
        var t = "kd_nb" + Guid.NewGuid().ToString("N")[..8];
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE {Quote(Schema)}.{Quote(t)} (a int NOT NULL, b text NOT NULL, CONSTRAINT {Quote("pk_" + t)} PRIMARY KEY (a)); " +
                              $"CREATE UNIQUE INDEX {Quote("ux_lower_" + t)} ON {Quote(Schema)}.{Quote(t)} (lower(b)); " +
                              $"CREATE INDEX {Quote("hx_" + t)} ON {Quote(Schema)}.{Quote(t)} USING hash (b);";
            await cmd.ExecuteNonQueryAsync();
        }

        var rows = new List<DbKeyIndexColumn>();
        await foreach (var c in Reader.ReadAsync(conn, new TableIdentity(Schema, t), CancellationToken.None))
        {
            rows.Add(c);
        }

        rows.Select(r => r.IndexName).Distinct().ShouldBe(new[] { "pk_" + t });
    }
}
