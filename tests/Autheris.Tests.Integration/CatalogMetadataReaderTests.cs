namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0c: the metadata readers against real databases (Testcontainers). The fixtures throw when no container runtime is
/// reachable, so these cases cannot pass by returning early.
/// </summary>
internal static class ReaderHelper
{
    public static async Task<List<DbColumnMetadata>> ReadAsync(IColumnMetadataReader reader, DbConnection connection, TableIdentity table)
    {
        var list = new List<DbColumnMetadata>();
        await foreach (var column in reader.ReadAsync(connection, table, CancellationToken.None))
        {
            list.Add(column);
        }

        return list;
    }

    public static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static (string Name, string Type, string? Collation)[] Shape(IEnumerable<DbColumnMetadata> columns) =>
        columns.Select(c => (c.Name, c.NativeType, c.Collation)).ToArray();
}

public sealed class PostgreSqlColumnMetadataReaderTests : IClassFixture<AstCompilerPostgreSqlFixture>
{
    private readonly AstCompilerPostgreSqlFixture _fx;

    public PostgreSqlColumnMetadataReaderTests(AstCompilerPostgreSqlFixture fixture) => _fx = fixture;

    [Fact]
    public async Task PostgreSql_ReadsNativeTypeAndCollation()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await ReaderHelper.ExecAsync(conn, """
            CREATE TABLE public.x0c_types (
                id integer PRIMARY KEY,
                name varchar(64),
                amount numeric(18,3),
                code text COLLATE "C",
                tenantid citext,
                "MixedCase" text);
            """);

        var columns = await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("public", "x0c_types"));

        ReaderHelper.Shape(columns).ShouldBe(new (string, string, string?)[]
        {
            ("id", "integer", null),
            ("name", "character varying(64)", "default"),
            ("amount", "numeric(18,3)", null),
            ("code", "text", "C"),
            ("tenantid", "citext", "default"),
            ("MixedCase", "text", "default")
        });
    }

    [Fact]
    public async Task PostgreSql_SchemaIsBound_SameNamedTableInOtherSchemaIsDistinct()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        var publicOrders = await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("public", "orders"));
        var evilOrders = await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("evil", "orders"));

        publicOrders.Single(c => c.Name == "tenantid").NativeType.ShouldBe("citext");
        evilOrders.Single(c => c.Name == "tenantid").NativeType.ShouldBe("text");
    }

    [Fact]
    public async Task PostgreSql_HostileNames_AreBoundAndReturnNothing()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        (await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("public", "orders' OR '1'='1"))).ShouldBeEmpty();
        (await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("public", "no_such_table"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task PostgreSql_ReaderIgnoresSearchPath_ShadowingRelationNamedLikeCatalog()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await ReaderHelper.ExecAsync(conn, """
            CREATE SCHEMA IF NOT EXISTS shadow;
            CREATE TABLE shadow.pg_attribute (attrelid oid, attname name, atttypid oid, atttypmod int, attnum int, attisdropped bool, attcollation oid);
            CREATE TABLE shadow.pg_class (oid oid, relname name, relnamespace oid);
            CREATE TABLE shadow.pg_namespace (oid oid, nspname name);
            CREATE TABLE shadow.pg_collation (oid oid, collname name);
            SET search_path = shadow, pg_catalog, public;
            """);

        var columns = await ReaderHelper.ReadAsync(new PostgreSqlColumnMetadataReader(), conn, new TableIdentity("public", "orders"));

        columns.Select(c => c.Name).ShouldBe(new[] { "id", "tenantid", "region", "status", "amount", "email" });
    }
}

public sealed class SqlServerColumnMetadataReaderTests : IClassFixture<AstCompilerSqlServerFixture>
{
    private readonly AstCompilerSqlServerFixture _fx;

    public SqlServerColumnMetadataReaderTests(AstCompilerSqlServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task SqlServer_ReadsPerColumnCollation()
    {
        _fx.Db.ShouldNotBeNull();
        _fx.Db.IsAvailable.ShouldBeTrue("SQL Server container is required for WP-X0c evidence");
        await using var conn = new SqlConnection(_fx.Db.ConnectionString);
        await conn.OpenAsync();
        await ReaderHelper.ExecAsync(conn, """
            CREATE TABLE dbo.X0cTypes (
                Id int NOT NULL,
                Name nvarchar(64) COLLATE Latin1_General_100_CS_AS NOT NULL,
                Code varchar(10) COLLATE Latin1_General_BIN2 NULL,
                Big nvarchar(max) NULL,
                Amount decimal(18,3) NULL,
                Raw varbinary(16) NULL,
                Stamp datetime2(3) NULL,
                Flag bit NULL,
                Ratio float NULL,
                Fixed char(5) NULL);
            """);

        var columns = await ReaderHelper.ReadAsync(new SqlServerColumnMetadataReader(), conn, new TableIdentity("dbo", "X0cTypes"));

        ReaderHelper.Shape(columns).ShouldBe(new (string, string, string?)[]
        {
            ("Id", "int", null),
            ("Name", "nvarchar(64)", "Latin1_General_100_CS_AS"),
            ("Code", "varchar(10)", "Latin1_General_BIN2"),
            ("Big", "nvarchar(max)", "SQL_Latin1_General_CP1_CI_AS"),
            ("Amount", "decimal(18,3)", null),
            ("Raw", "varbinary(16)", null),
            ("Stamp", "datetime2(3)", null),
            ("Flag", "bit", null),
            ("Ratio", "float", null),
            ("Fixed", "char(5)", "SQL_Latin1_General_CP1_CI_AS")
        });
    }

    [Fact]
    public async Task SqlServer_ExistingFixtureTable_TenantColumnIsCaseInsensitive()
    {
        _fx.Db.ShouldNotBeNull();
        _fx.Db.IsAvailable.ShouldBeTrue("SQL Server container is required for WP-X0c evidence");
        await using var conn = new SqlConnection(_fx.Db.ConnectionString);
        await conn.OpenAsync();

        var columns = await ReaderHelper.ReadAsync(new SqlServerColumnMetadataReader(), conn, new TableIdentity("dbo", "Orders"));

        var tenant = columns.Single(c => c.Name == "TenantId");
        tenant.NativeType.ShouldBe("nvarchar(64)");
        tenant.Collation.ShouldBe("SQL_Latin1_General_CP1_CI_AS");
        columns.Single(c => c.Name == "Amount").NativeType.ShouldBe("decimal(18,2)");
        (await ReaderHelper.ReadAsync(new SqlServerColumnMetadataReader(), conn, new TableIdentity("dbo", "Orders'; --"))).ShouldBeEmpty();
    }
}

public sealed class OracleColumnMetadataReaderTests : IClassFixture<AstCompilerOracleFixture>
{
    private readonly AstCompilerOracleFixture _fx;

    public OracleColumnMetadataReaderTests(AstCompilerOracleFixture fixture) => _fx = fixture;

    private Task<DbConnection> Open() =>
        new SqlConnectionFactory(new DevelopmentEnvironment()).CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = _fx.AppConnectionString });

    [Fact]
    public async Task Oracle_ReadsCharUsedAndCollation()
    {
        await using var conn = await Open();
        await ReaderHelper.ExecAsync(conn, """
            CREATE TABLE AUTH_APP.X0C_TYPES (
                ID NUMBER(10) NOT NULL,
                AMOUNT NUMBER(18,3),
                ANY_NUM NUMBER,
                BYTE_COL VARCHAR2(10 BYTE),
                CHAR_COL VARCHAR2(10 CHAR),
                NLS_COL VARCHAR2(20 CHAR),
                STAMP TIMESTAMP(6),
                RAW_COL RAW(16))
            """);

        var columns = await ReaderHelper.ReadAsync(new OracleColumnMetadataReader(), conn, new TableIdentity("AUTH_APP", "X0C_TYPES"));

        columns.Select(c => (c.Name, c.NativeType)).ShouldBe(new[]
        {
            ("ID", "number(10)"), ("AMOUNT", "number(18,3)"), ("ANY_NUM", "number"), ("BYTE_COL", "varchar2(10)"),
            ("CHAR_COL", "varchar2(10)"), ("NLS_COL", "varchar2(20)"), ("STAMP", "timestamp(6)"), ("RAW_COL", "raw(16)")
        });
        columns.Single(c => c.Name == "BYTE_COL").LengthSemantics.ShouldBe("byte");
        columns.Single(c => c.Name == "CHAR_COL").LengthSemantics.ShouldBe("char");
        // Column-level collations need MAX_STRING_SIZE=EXTENDED (ORA-43929 on the Free image); a character column then reports
        // the session-dependent USING_NLS_COMP, which is never treated as binary.
        columns.Single(c => c.Name == "NLS_COL").Collation.ShouldBe("USING_NLS_COMP");
        columns.Single(c => c.Name == "ID").LengthSemantics.ShouldBeNull();
        columns.Single(c => c.Name == "ID").Collation.ShouldBeNull();
    }

    [Fact]
    public async Task Oracle_ReaderUsesSysQualifiedView_ShadowingTableIgnored()
    {
        await using var conn = await Open();
        await ReaderHelper.ExecAsync(conn, """
            CREATE TABLE AUTH_APP.ALL_TAB_COLUMNS (
                OWNER VARCHAR2(128), TABLE_NAME VARCHAR2(128), COLUMN_NAME VARCHAR2(128), DATA_TYPE VARCHAR2(128),
                DATA_LENGTH NUMBER, CHAR_LENGTH NUMBER, CHAR_USED VARCHAR2(1), DATA_PRECISION NUMBER, DATA_SCALE NUMBER,
                COLUMN_ID NUMBER, COLLATION VARCHAR2(100))
            """);
        await ReaderHelper.ExecAsync(conn, "INSERT INTO AUTH_APP.ALL_TAB_COLUMNS VALUES ('AUTH_APP', 'ORDERS', 'FAKE', 'CLOB', 1, 1, 'B', NULL, NULL, 1, 'FAKE_COLLATION')");

        // The unqualified name resolves to the shadowing table: this is the attack the qualified view prevents.
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE TABLE_NAME = 'ORDERS'";
            (await probe.ExecuteScalarAsync()).ShouldBe("FAKE");
        }

        var columns = await ReaderHelper.ReadAsync(new OracleColumnMetadataReader(), conn, new TableIdentity("AUTH_APP", "ORDERS"));

        columns.Select(c => c.Name).ShouldBe(new[] { "ID", "TENANT_ID", "REGION", "STATUS", "AMOUNT", "EMAIL" });
        columns.ShouldNotContain(c => c.Name == "FAKE");
        columns.Single(c => c.Name == "AMOUNT").NativeType.ShouldBe("number(18,2)");
        columns.Single(c => c.Name == "TENANT_ID").NativeType.ShouldBe("varchar2(64)");
    }

    [Fact]
    public async Task Oracle_UnknownTableAndHostileName_ReturnNothing()
    {
        await using var conn = await Open();

        (await ReaderHelper.ReadAsync(new OracleColumnMetadataReader(), conn, new TableIdentity("AUTH_APP", "NOPE"))).ShouldBeEmpty();
        (await ReaderHelper.ReadAsync(new OracleColumnMetadataReader(), conn, new TableIdentity("AUTH_APP", "ORDERS' OR '1'='1"))).ShouldBeEmpty();
    }
}
