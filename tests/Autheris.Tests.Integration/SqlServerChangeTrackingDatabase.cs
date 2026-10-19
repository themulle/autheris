namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;

/// <summary>
/// REQ-CDC-99 (WP-I1-B): a fresh SQL Server database with change tracking (<c>CHANGE_RETENTION = 2 DAYS</c>) and, optionally, snapshot
/// isolation, plus a least-privilege login (<c>CONNECT</c>, <c>SELECT</c>, <c>VIEW CHANGE TRACKING</c> on the tracked tables only) and a
/// helper that creates tracked tables. <see cref="IsAvailable"/> is false without a SQL Server (the tests then return early).
/// </summary>
public sealed class SqlServerChangeTrackingDatabase : IAsyncDisposable
{
    private const string Password = "Cdc#Test-Passw0rd!";

    private readonly List<string> _logins = [];
    private SqlServerTestDatabase? _db;

    public bool IsAvailable { get; private set; }

    /// <summary>Administrator connection string of the database.</summary>
    public string AdminConnectionString => _db!.ConnectionString;

    public static async Task<SqlServerChangeTrackingDatabase> CreateAsync(bool snapshotIsolation = true)
    {
        var helper = new SqlServerChangeTrackingDatabase();
        helper._db = await SqlServerTestDatabase.CreateAsync();
        helper.IsAvailable = helper._db.IsAvailable;
        if (!helper.IsAvailable)
        {
            return helper;
        }

        var name = new SqlConnectionStringBuilder(helper._db.ConnectionString).InitialCatalog;
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(helper._db.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await ExecuteAsync(master, $"ALTER DATABASE [{name}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
        await ExecuteAsync(master, $"ALTER DATABASE [{name}] SET ALLOW_SNAPSHOT_ISOLATION {(snapshotIsolation ? "ON" : "OFF")}");
        return helper;
    }

    /// <summary>Creates <c>dbo.name(Id int primary key, Name nvarchar(100), TenantId nvarchar(64))</c>, optionally change tracked.</summary>
    public async Task CreateTableAsync(string name, bool changeTracking = true)
    {
        await using var conn = new SqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, $"CREATE TABLE dbo.[{name}] (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NULL, TenantId nvarchar(64) NULL)");
        if (changeTracking)
        {
            await EnableChangeTrackingAsync(name);
        }
    }

    public async Task EnableChangeTrackingAsync(string table)
    {
        await using var conn = new SqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, $"ALTER TABLE dbo.[{table}] ENABLE CHANGE_TRACKING");
    }

    public async Task ExecuteAdminAsync(string sql)
    {
        await using var conn = new SqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, sql);
    }

    /// <summary>
    /// Creates a login and user holding exactly <c>CONNECT</c>, <c>SELECT</c> and (unless <paramref name="withViewChangeTracking"/> is false)
    /// <c>VIEW CHANGE TRACKING</c> on the listed tables; returns its connection string.
    /// </summary>
    public async Task<string> CreateLeastPrivilegeLoginAsync(IEnumerable<string> tables, bool withViewChangeTracking = true)
    {
        var login = "cdc_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new SqlConnectionStringBuilder(AdminConnectionString);
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await ExecuteAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF");
            _logins.Add(login);
        }

        await using var conn = new SqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, $"CREATE USER [{login}] FOR LOGIN [{login}]");
        foreach (var table in tables)
        {
            await ExecuteAsync(conn, $"GRANT SELECT ON dbo.[{table}] TO [{login}]");
            if (withViewChangeTracking)
            {
                await ExecuteAsync(conn, $"GRANT VIEW CHANGE TRACKING ON dbo.[{table}] TO [{login}]");
            }
        }

        builder.UserID = login;
        builder.Password = Password;
        builder.IntegratedSecurity = false;
        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is null || !IsAvailable)
        {
            return;
        }

        try
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = "master" }.ConnectionString);
            await master.OpenAsync();
            foreach (var login in _logins)
            {
                await ExecuteAsync(master, $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') DROP LOGIN [{login}]");
            }
        }
        catch (Exception)
        {
            // best effort: the shared container is removed at process exit
        }

        await _db.DisposeAsync();
    }
}

/// <summary>An <see cref="ISqlConnectionFactory"/> over SqlClient that records every statement text (a counting decorator, no XEvents).</summary>
public sealed class RecordingSqlConnectionFactory : ISqlConnectionFactory
{
    public List<string> Statements { get; } = [];

    public int Connections { get; private set; }

    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        var inner = new SqlConnection(options.ConnectionString);
        await inner.OpenAsync(ct);
        Connections++;
        return new RecordingConnection(inner, Statements);
    }

    private sealed class RecordingConnection(SqlConnection inner, List<string> statements) : DbConnection
    {
#pragma warning disable CS8765
        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
#pragma warning restore CS8765

        public override string Database => inner.Database;

        public override string DataSource => inner.DataSource;

        public override string ServerVersion => inner.ServerVersion;

        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        public override void Close() => inner.Close();

        public override void Open() => inner.Open();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => new RecordingCommand(inner.CreateCommand(), this, statements);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RecordingCommand(SqlCommand inner, DbConnection connection, List<string> statements) : DbCommand
    {
#pragma warning disable CS8765
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
#pragma warning restore CS8765

        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }

        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }

        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }

        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }

        protected override DbConnection? DbConnection { get => connection; set { } }

        protected override DbParameterCollection DbParameterCollection => inner.Parameters;

        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = (SqlTransaction?)value; }

        public override void Cancel() => inner.Cancel();

        public override int ExecuteNonQuery()
        {
            statements.Add(CommandText);
            return inner.ExecuteNonQuery();
        }

        public override object? ExecuteScalar()
        {
            statements.Add(CommandText);
            return inner.ExecuteScalar();
        }

        public override void Prepare() => inner.Prepare();

        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            statements.Add(CommandText);
            return inner.ExecuteReader(behavior);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
