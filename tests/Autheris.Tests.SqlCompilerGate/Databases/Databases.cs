namespace Autheris.Tests.SqlCompilerGate.Databases;

using System.Data.Common;
using Autheris.Tests.SqlCompilerGate.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.MsSql;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

/// <summary>SQLite in process: a shared-cache in-memory database that lives as long as the keeper connection.</summary>
#pragma warning disable CA1001 // the keeper connection is disposed by StopAsync (IAsyncLifetime)
public sealed class SqliteGateDatabase : GateDatabase
{
    private readonly string _connectionString = $"Data Source=gate-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;

    public override GateDialect Dialect => GateDialect.Sqlite;
    public override string DefaultSchema => "main";

    protected override async Task StartAsync()
    {
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
    }

    protected override async Task StopAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    public override async Task<DbConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }
}

#pragma warning restore CA1001

public sealed class PostgreSqlGateDatabase : GateDatabase
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("sql_gate")
        .WithUsername("postgres")
        .WithPassword("postgres_password_2026")
        .Build();

    public override GateDialect Dialect => GateDialect.PostgreSql;
    public override string DefaultSchema => "public";

    protected override Task StartAsync() => _container.StartAsync();

    protected override async Task StopAsync() => await _container.DisposeAsync();

    public override async Task<DbConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }
}

public sealed class SqlServerGateDatabase : GateDatabase
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public override GateDialect Dialect => GateDialect.SqlServer;
    public override string DefaultSchema => "dbo";

    protected override Task StartAsync() => _container.StartAsync();

    protected override async Task StopAsync() => await _container.DisposeAsync();

    public override async Task<DbConnection> OpenAsync()
    {
        var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }
}

/// <summary>Oracle Free with an application user that owns the fixture (the default schema of that user is the catalog's).</summary>
public sealed class OracleGateDatabase : GateDatabase
{
    private const string AppUser = "GATE_APP";
    private const string AppPassword = "Gate_App_Password_2026";
    private const string AdminPassword = "Oracle_Password_2026";

    // The digest the Oracle execution suites pin (SEC-ADG-24).
    private const string Image = "gvenzl/oracle-free:23-slim-faststart@sha256:d86d09794ae138a8951e97d7ee010778dcd43088e8d0f83a51002abab8c6d7fd";

    private readonly OracleContainer _container = new OracleBuilder(Image).WithPassword(AdminPassword).Build();
    private string _dataSource = string.Empty;

    public override GateDialect Dialect => GateDialect.Oracle;
    public override string DefaultSchema => AppUser;

    protected override async Task StartAsync()
    {
        await _container.StartAsync();
        _dataSource = $"{_container.Hostname}:{_container.GetMappedPublicPort(1521)}/FREEPDB1";
        await using var admin = new OracleConnection($"User Id=system;Password={AdminPassword};Data Source={_dataSource}");
        await admin.OpenAsync();
        foreach (var sql in new[]
        {
            $"CREATE USER {AppUser} IDENTIFIED BY {AppPassword} QUOTA UNLIMITED ON USERS",
            $"GRANT CREATE SESSION, ALTER SESSION, CREATE TABLE TO {AppUser}"
        })
        {
            await ExecAsync(admin, sql);
        }
    }

    protected override async Task StopAsync() => await _container.DisposeAsync();

    public override async Task<DbConnection> OpenAsync()
    {
        var connection = new OracleConnection($"User Id={AppUser};Password={AppPassword};Data Source={_dataSource}");
        await connection.OpenAsync();
        return connection;
    }
}
