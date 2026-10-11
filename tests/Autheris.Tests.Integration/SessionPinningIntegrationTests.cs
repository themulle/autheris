namespace Autheris.Tests.Integration;

using System;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Xunit;

/// <summary>WP-X0b: the connection factory pins schema and session options on every rental (PostgreSQL).</summary>
public sealed class PostgreSqlSessionPinningTests : IClassFixture<AstCompilerPostgreSqlFixture>
{
    private readonly AstCompilerPostgreSqlFixture _fx;
    private readonly SqlConnectionFactory _factory = new(new DevelopmentEnvironment());

    public PostgreSqlSessionPinningTests(AstCompilerPostgreSqlFixture fixture) => _fx = fixture;

    private DataSourceConnectionOptions Options(string? schema) => new()
    {
        Provider = "PostgreSql",
        ConnectionString = new NpgsqlConnectionStringBuilder(_fx.ConnectionString) { MaxPoolSize = 1, Pooling = true }.ConnectionString,
        DefaultSchema = schema,
        RequireDefaultSchema = true
    };

    private static async Task<object?> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task PostgreSql_PinsSearchPath_PgCatalogFirstPgTempLast()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(Options("evil"));

        (await ScalarAsync(conn, "SELECT current_setting('search_path')")).ShouldBe("pg_catalog, evil, pg_temp");
        Convert.ToInt32(await ScalarAsync(conn, "SELECT id FROM orders")).ShouldBe(99);
    }

    [Fact]
    public async Task PostgreSql_SearchPath_SchemaIsQuotedByTheServer()
    {
        await using (var admin = new NpgsqlConnection(_fx.ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS \"Mixed_Case\"; CREATE TABLE IF NOT EXISTS \"Mixed_Case\".orders (id integer); " +
                              "DELETE FROM \"Mixed_Case\".orders; INSERT INTO \"Mixed_Case\".orders VALUES (7)";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var conn = await _factory.CreateOpenConnectionAsync(Options("Mixed_Case"));

        (await ScalarAsync(conn, "SELECT current_setting('search_path')")).ShouldBe("pg_catalog, \"Mixed_Case\", pg_temp");
        Convert.ToInt32(await ScalarAsync(conn, "SELECT id FROM orders")).ShouldBe(7);
    }

    [Fact]
    public async Task Factory_PinsSchemaOnEveryRental_WithoutSessionInitializer()
    {
        for (int i = 0; i < 3; i++)
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(Options("evil"));
            Convert.ToInt32(await ScalarAsync(conn, "SELECT id FROM orders")).ShouldBe(99);
        }
    }

    [Fact]
    public async Task PooledConnection_HostileSessionState_RepinnedOnRental()
    {
        await using (var first = await _factory.CreateOpenConnectionAsync(Options("evil")))
        {
            await using var hostile = first.CreateCommand();
            hostile.CommandText = "SET search_path = public";
            await hostile.ExecuteNonQueryAsync();
        }

        await using var second = await _factory.CreateOpenConnectionAsync(Options("evil"));

        (await ScalarAsync(second, "SELECT current_setting('search_path')")).ShouldBe("pg_catalog, evil, pg_temp");
    }

    [Fact]
    public async Task MissingDefaultSchemaAtRequestTime_FailsClosed()
    {
        await Should.ThrowAsync<Autheris.Application.Interfaces.SqlSourceUnavailableException>(() => _factory.CreateOpenConnectionAsync(Options(null)));
    }
}

/// <summary>WP-X0b: SQL Server ANSI options are pinned and verified on every rental.</summary>
public sealed class SqlServerSessionPinningTests : IClassFixture<AstCompilerSqlServerFixture>
{
    private readonly AstCompilerSqlServerFixture _fx;
    private readonly SqlConnectionFactory _factory = new(new DevelopmentEnvironment());

    public SqlServerSessionPinningTests(AstCompilerSqlServerFixture fixture) => _fx = fixture;

    private DataSourceConnectionOptions Options() => new()
    {
        Provider = "SqlServer",
        ConnectionString = new SqlConnectionStringBuilder(_fx.Db!.ConnectionString) { MaxPoolSize = 1, Pooling = true }.ConnectionString
    };

    private static async Task<int> OptionsAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@OPTIONS";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static void AssertPinned(int options)
    {
        (options & 4472).ShouldBe(4472);
        (options & 8192).ShouldBe(0);
    }

    [Fact]
    public async Task SqlServer_PinsAnsiOptions_AndVerifies()
    {
        if (!_fx.Db!.IsAvailable) return;

        await using var conn = await _factory.CreateOpenConnectionAsync(Options());

        AssertPinned(await OptionsAsync(conn));
    }

    [Fact]
    public async Task PooledConnection_HostileSessionState_RepinnedOnRental()
    {
        if (!_fx.Db!.IsAvailable) return;

        await using (var first = await _factory.CreateOpenConnectionAsync(Options()))
        {
            await using var hostile = first.CreateCommand();
            hostile.CommandText = "SET ANSI_WARNINGS OFF; SET NUMERIC_ROUNDABORT ON; SET ARITHABORT OFF";
            await hostile.ExecuteNonQueryAsync();
        }

        await using var second = await _factory.CreateOpenConnectionAsync(Options());

        AssertPinned(await OptionsAsync(second));
    }
}

/// <summary>WP-X0b: Oracle CURRENT_SCHEMA is pinned (existence proven by DBMS_ASSERT) on every rental.</summary>
public sealed class OracleSessionPinningTests : IClassFixture<AstCompilerOracleFixture>
{
    private readonly AstCompilerOracleFixture _fx;
    private readonly SqlConnectionFactory _factory = new(new DevelopmentEnvironment());

    public OracleSessionPinningTests(AstCompilerOracleFixture fixture) => _fx = fixture;

    private DataSourceConnectionOptions Options(string? schema) => new()
    {
        Provider = "Oracle",
        ConnectionString = _fx.AppConnectionString,
        DefaultSchema = schema,
        RequireDefaultSchema = true
    };

    private static async Task<string?> CurrentSchemaAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    [Fact]
    public async Task Oracle_PinsCurrentSchema_AndVerifies()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(Options("SYSTEM"));

        (await CurrentSchemaAsync(conn)).ShouldBe("SYSTEM");
    }

    [Fact]
    public async Task Oracle_NonExistentSchema_FailsClosed()
    {
        await Should.ThrowAsync<OracleException>(() => _factory.CreateOpenConnectionAsync(Options("NO_SUCH_SCHEMA_X0B")));

        // The pool of size one is still usable: the failed connection was disposed, not leaked.
        await using var conn = await _factory.CreateOpenConnectionAsync(Options(AstCompilerOracleFixture.AppUser));
        (await CurrentSchemaAsync(conn)).ShouldBe(AstCompilerOracleFixture.AppUser);
    }

    [Fact]
    public async Task PooledConnection_HostileSessionState_RepinnedOnRental()
    {
        await using (var first = await _factory.CreateOpenConnectionAsync(Options(AstCompilerOracleFixture.AppUser)))
        {
            await using var hostile = first.CreateCommand();
            hostile.CommandText = "ALTER SESSION SET CURRENT_SCHEMA = SYSTEM";
            await hostile.ExecuteNonQueryAsync();
        }

        await using var second = await _factory.CreateOpenConnectionAsync(Options(AstCompilerOracleFixture.AppUser));

        (await CurrentSchemaAsync(second)).ShouldBe(AstCompilerOracleFixture.AppUser);
    }
}
