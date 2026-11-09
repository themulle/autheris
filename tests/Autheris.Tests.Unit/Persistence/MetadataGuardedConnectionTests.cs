using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.Sql;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>SEC-SIMPL-141: every connection of the factory is guarded; a violation never reaches the driver and never logs SQL.</summary>
[Trait("Category", "Security")]
public sealed class MetadataGuardedConnectionTests
{
    private const string Catalog = "SELECT secret_marker FROM information_schema.columns";

    [Fact]
    public async Task SEC_SIMPL_141_RuntimeGuard_denies_system_catalog_reads_and_never_logs_sql()
    {
        var rules = new List<string>();
        var inner = new CountingConnection();
        await using var connection = new MetadataGuardedConnection(inner, DatabaseDialect.PostgreSql, rules.Add);
        await using var command = connection.CreateCommand();
        command.CommandText = Catalog;

        var ex = await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteReaderAsync());

        inner.Executions.ShouldBe(0);
        rules.ShouldBe(["R1"]);
        ex.Message.ShouldNotContain("secret_marker");
        ex.Message.ShouldNotContain("information_schema");
    }

    [Fact]
    public async Task SEC_SIMPL_141_ExecuteNonQueryScalarPrepare_AllGuarded()
    {
        var inner = new CountingConnection();
        await using var connection = new MetadataGuardedConnection(inner, DatabaseDialect.SqlServer);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sys.tables";

        Should.Throw<SourceMetadataAccessDeniedException>(() => { command.ExecuteNonQuery(); });
        Should.Throw<SourceMetadataAccessDeniedException>(() => { command.ExecuteScalar(); });
        Should.Throw<SourceMetadataAccessDeniedException>(() => command.Prepare());
        Should.Throw<SourceMetadataAccessDeniedException>(() => { command.ExecuteReader(); });
        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteNonQueryAsync());
        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteScalarAsync());
        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteReaderAsync());
        inner.Executions.ShouldBe(0);

        command.CommandText = "SELECT 1";
        command.ExecuteScalar();
        inner.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task SEC_SIMPL_141_SchemaOnlyAndKeyInfoBehavior_AreRejected()
    {
        var inner = new CountingConnection();
        await using var connection = new MetadataGuardedConnection(inner, DatabaseDialect.Sqlite);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        Should.Throw<SourceMetadataAccessDeniedException>(() => { command.ExecuteReader(CommandBehavior.SchemaOnly); }).Rule.ShouldBe("R7");
        Should.Throw<SourceMetadataAccessDeniedException>(() => { command.ExecuteReader(CommandBehavior.KeyInfo); }).Rule.ShouldBe("R7");
        inner.Executions.ShouldBe(0);
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "SELECT * FROM pg_catalog.pg_class")]
    [InlineData(DatabaseDialect.SqlServer, "SELECT * FROM sys.objects")]
    [InlineData(DatabaseDialect.Oracle, "SELECT * FROM all_tab_columns")]
    [InlineData(DatabaseDialect.Databricks, "SHOW TABLES")]
    [InlineData(DatabaseDialect.Sqlite, "SELECT * FROM information_schema.columns")]
    public async Task SEC_SIMPL_141_Guard_AppliesDialectRules_ForEveryDialect(DatabaseDialect dialect, string sql)
    {
        var inner = new CountingConnection();
        await using var connection = new MetadataGuardedConnection(inner, dialect);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteScalarAsync());
        inner.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task SEC_SIMPL_141_Factory_ReturnsGuardedConnection_AndSessionPinsRunOnRawConnection()
    {
        var factory = new SqlConnectionFactory();
        await using var connection = await factory.CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" });

        // the SQLite pins (PRAGMA trusted_schema and its read-back) ran on the raw connection while opening.
        connection.ShouldBeOfType<MetadataGuardedConnection>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        (await command.ExecuteScalarAsync()).ShouldBe(1L);
        command.CommandText = Catalog;
        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SEC_SIMPL_141_Violation_EmitsAuditAndWarning_WithoutSql()
    {
        var logger = new ListLogger();
        var audit = Substitute.For<IAuditWriter>();
        AuditLogEntry? entry = null;
        audit.RecordBestEffortAsync(Arg.Do<AuditLogEntry>(e => entry = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddSingleton<ILogger<SqlConnectionFactory>>(logger)
            .AddSingleton(audit)
            .BuildServiceProvider();
        var factory = new SqlConnectionFactory(null, null, services);
        await using var connection = await factory.CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" });
        await using var command = connection.CreateCommand();
        command.CommandText = Catalog;

        await Should.ThrowAsync<SourceMetadataAccessDeniedException>(() => command.ExecuteScalarAsync());

        logger.Messages.ShouldContain(m => m.Contains("SourceMetadataReadBlocked", StringComparison.Ordinal) && m.Contains("R1", StringComparison.Ordinal));
        logger.Messages.ShouldAllBe(m => !m.Contains("secret_marker", StringComparison.Ordinal) && !m.Contains("information_schema", StringComparison.OrdinalIgnoreCase));
        entry.ShouldNotBeNull();
        entry.EventType.ShouldBe(AuditEventTypes.SourceMetadataReadBlocked);
        entry.DetailsJson.ShouldContain("\"rule\":\"R1\"");
        entry.DetailsJson.ShouldNotContain("secret_marker");
        entry.DetailsJson.ShouldNotContain("information_schema");
    }

    [Fact]
    public void PostgreSql_ConnectionString_UsesNoTypeLoading()
    {
        var connectionString = SqlConnectionFactory.WithNoTypeLoading("Host=h;Database=d;Username=u");

        connectionString.ShouldContain("Server Compatibility Mode=NoTypeLoading");
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Host.ShouldBe("h");
    }

    private sealed class ListLogger : ILogger<SqlConnectionFactory>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CountingConnection : DbConnection
    {
        public int Executions { get; set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "d";
        public override string DataSource => "s";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new CountingCommand(this) { Connection = this };
    }

    private sealed class CountingCommand(CountingConnection owner) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() => owner.Executions++;
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => owner.Executions++;
        public override object? ExecuteScalar() { owner.Executions++; return 1L; }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { owner.Executions++; throw new NotSupportedException(); }
    }
}
