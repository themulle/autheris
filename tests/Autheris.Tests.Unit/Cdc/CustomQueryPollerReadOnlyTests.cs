#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.Cdc.CustomQuery;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

[Trait("Category", "Security")]
public sealed class CustomQueryPollerReadOnlyTests
{
    private readonly InMemoryCdcStreamStore _streamStore = new();
    private readonly ICdcStreamSignal _signal = Substitute.For<ICdcStreamSignal>();
    private readonly IDistributedClusterStateProvider _clusterState = Substitute.For<IDistributedClusterStateProvider>();
    private readonly CdcChangeEventBuilder _builder;

    public CustomQueryPollerReadOnlyTests()
    {
        var crypto = Substitute.For<ICdcStreamCrypto>();
        crypto.FingerprintAsync(Arg.Any<CdcStreamId>(), Arg.Any<IReadOnlyList<object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new byte[32]));
        crypto.ProtectEventAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<CdcStreamEventKind>(), Arg.Any<CdcTenantScope>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(6).ToArray())));
        crypto.ProtectImageAsync(Arg.Any<CdcStreamId>(), Arg.Any<byte[]>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(2).ToArray())));

        var codec = Substitute.For<ICdcPayloadCodec>();
        codec.SerializeChange(Arg.Any<CdcChangePayload>()).Returns(new byte[16]);
        codec.SerializeImage(Arg.Any<IReadOnlyDictionary<string, object?>>()).Returns(new byte[16]);

        var gatewayMonitor = CreateGatewayMonitor("src", "PostgreSql");
        _builder = new CdcChangeEventBuilder(_streamStore, crypto, codec, gatewayMonitor);
    }

    private static CustomQueryPollerStreamOptions ValidPoller(string dataSource = "src", string query = "SELECT id, updated_at FROM customers WHERE id > @p_wm_id") => new()
    {
        StreamName = "crm_customers",
        Table = "crm.dbo.customers",
        DataSource = dataSource,
        QueryTemplate = query,
        KeyColumns = ["id"],
        Watermarks =
        [
            new() { Name = "id", Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_id", Column = "id", InitialValue = "0" }
        ],
        OwnerTenantId = "tenant-a",
        BatchSize = 50,
        PollingIntervalMilliseconds = 500
    };

    private static IOptionsMonitor<GatewayOptions> CreateGatewayMonitor(string dataSourceName, string provider, string connString = "Server=localhost;Database=crm;")
    {
        var gateway = new GatewayOptions();
        gateway.DataSources.Connections[dataSourceName] = new DataSourceConnectionOptions
        {
            Provider = provider,
            ConnectionString = connString
        };
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(gateway);
        return monitor;
    }

    private CustomQueryPollerService CreateService(
        IOptionsMonitor<CustomQueryPollerOptions> pollerOptions,
        IDataSourceConnectionFactory factory,
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        CustomQueryPollerValidator? validator = null,
        ITableMetadataRepository? metadataRepository = null,
        ILogger<CustomQueryPollerService>? logger = null)
    {
        return new CustomQueryPollerService(
            pollerOptions,
            _streamStore,
            _builder,
            factory,
            _signal,
            _clusterState,
            logger ?? NullLogger<CustomQueryPollerService>.Instance,
            gatewayOptions: gatewayOptions,
            validator: validator ?? new CustomQueryPollerValidator(FastSqlEngine.Default),
            tableMetadataRepository: metadataRepository);
    }

    [Theory]
    [InlineData("Databricks")]
    [InlineData("unknown-provider")]
    [InlineData("")]
    public async Task Unsupported_provider_throws(string provider)
    {
        var poller = ValidPoller("src");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gateway = CreateGatewayMonitor("src", provider);
        var conn = new TrackingDbConnection("Host=example;Database=test;");
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(conn);

        using var service = CreateService(monitor, factory, gateway);

        await Should.ThrowAsync<NotSupportedException>(() => service.PollStreamBatchAsync(poller, fencingToken: 1));
    }

    [Fact]
    public async Task Provider_is_not_sniffed_from_host_name()
    {
        // SQL Server data source pointing to host named "postgres-db" and using a misleading connection string/DbConnection type
        var poller = ValidPoller("sqlsrv_host_postgres");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gateway = CreateGatewayMonitor("sqlsrv_host_postgres", "SqlServer", "Server=postgres-db;Database=sqlite_trap;");

        var conn = new PostgresLookingTrackingConnection("Server=postgres-db;Data Source=sqlite_trap;");
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("sqlsrv_host_postgres", Arg.Any<CancellationToken>()).Returns(conn);

        using var service = CreateService(monitor, factory, gateway);
        await service.PollStreamBatchAsync(poller, fencingToken: 1);

        // Must NOT issue PostgreSQL SET TRANSACTION READ ONLY or SQLite PRAGMA query_only based on connection string/type sniffing
        conn.ExecutedCommands.ShouldNotContain(c => c.Contains("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase));
        conn.ExecutedCommands.ShouldNotContain(c => c.Contains("PRAGMA query_only", StringComparison.OrdinalIgnoreCase));
        // Must follow SQL Server behavior: explicit transaction, attached to command, rolled back and never committed
        conn.Transactions.Count.ShouldBe(1);
        conn.Transactions[0].RollbackCount.ShouldBe(1);
        conn.Transactions[0].CommitCount.ShouldBe(0);
    }

    [Fact]
    public async Task Validator_runs_before_first_poll_and_on_reload()
    {
        var initialValid = ValidPoller("src");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [initialValid] });
        var gateway = CreateGatewayMonitor("src", "PostgreSql", "Host=localhost");
        var conn = new TrackingDbConnection("Data Source=:memory:");
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("src", Arg.Any<CancellationToken>()).Returns(conn);

        var spyEngine = new SpySqlEngine(FastSqlEngine.Default);
        var validator = new CustomQueryPollerValidator(spyEngine);

        using var service = new TestablePollerService(
            monitor, _streamStore, _builder, factory, _signal, _clusterState, gateway, validator);

        // Initial construction/load validates the stream once
        var parsesAtInit = spyEngine.ParseCalls;
        parsesAtInit.ShouldBeGreaterThanOrEqualTo(1);

        // Repeated identical poll reuses hash cache (no duplicate parse per steady-state poll)
        await service.PollStreamBatchAsync(initialValid, fencingToken: 1);
        await service.PollStreamBatchAsync(initialValid, fencingToken: 1);
        spyEngine.ParseCalls.ShouldBe(parsesAtInit);

        // Reload with changed valid query invalidates hash cache and re-runs validator
        var reloadedValid = ValidPoller("src", "SELECT id, updated_at FROM customers WHERE id >= @p_wm_id");
        monitor.Update(new CustomQueryPollerOptions { Enabled = true, Pollers = [reloadedValid] });

        spyEngine.ParseCalls.ShouldBeGreaterThan(parsesAtInit);

        // Executing partition runs the newly validated query
        await service.ExecuteConfiguredPartitionAsync("crm_customers", 1);
        conn.ExecutedCommands.ShouldContain(c => c.Contains("WHERE id >= @p_wm_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_reload_keeps_last_valid_poller()
    {
        var validPoller = ValidPoller("src", "SELECT id, updated_at FROM customers WHERE id > @p_wm_id");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [validPoller] });
        var gateway = CreateGatewayMonitor("src", "PostgreSql", "Host=localhost");

        var conn = new TrackingDbConnection("Data Source=:memory:");
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("src", Arg.Any<CancellationToken>()).Returns(conn);

        using var service = new TestablePollerService(
            monitor, _streamStore, _builder, factory, _signal, _clusterState, gateway, new CustomQueryPollerValidator(FastSqlEngine.Default));

        // Push invalid reload (DROP TABLE injection)
        var invalidPoller = ValidPoller("src", "SELECT id FROM customers WHERE id > @p_wm_id; DROP TABLE customers;");
        monitor.Update(new CustomQueryPollerOptions { Enabled = true, Pollers = [invalidPoller] });

        // Active partition must keep the last valid poller and never execute the invalid query text
        await service.ExecuteConfiguredPartitionAsync("crm_customers", 1);

        conn.ExecutedCommands.ShouldContain(c => c.Contains("SELECT id, updated_at FROM customers WHERE id > @p_wm_id", StringComparison.Ordinal));
        conn.ExecutedCommands.ShouldNotContain(c => c.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Multi_statement_template_rejected_on_sql_server()
    {
        var malicious = ValidPoller("sqlsrv", "SELECT id FROM customers WHERE id > @p_wm_id; DELETE FROM customers;");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [malicious] });
        var gateway = CreateGatewayMonitor("sqlsrv", "SqlServer");
        var factory = Substitute.For<IDataSourceConnectionFactory>();

        using var service = CreateService(monitor, factory, gateway);

        await Should.ThrowAsync<GovernedCdcConfigurationException>(() => service.PollStreamBatchAsync(malicious, fencingToken: 1));
        await factory.DidNotReceiveWithAnyArgs().CreateConnectionAsync(default!, default);
    }

    [Fact]
    public async Task Write_connection_never_used()
    {
        // Only DataSources.Connections (read-only configured sources) is ever used; missing/unregistered source fails closed
        var poller = ValidPoller("read_src");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gatewayOptions = new GatewayOptions();
        gatewayOptions.DataSources.Connections["read_src"] = new DataSourceConnectionOptions
        {
            Provider = "PostgreSql",
            ConnectionString = "Host=ro-replica;Database=crm;"
        };
        var gatewayMonitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        gatewayMonitor.CurrentValue.Returns(gatewayOptions);

        var sqlConnFactory = Substitute.For<ISqlConnectionFactory>();
        var capturedOptions = new List<DataSourceConnectionOptions>();
        sqlConnFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions.Add(call.Arg<DataSourceConnectionOptions>());
                return Task.FromResult<DbConnection>(new TrackingDbConnection("Host=ro-replica;"));
            });

        var factory = new DefaultDataSourceConnectionFactory(sqlConnFactory, gatewayMonitor);
        using var service = CreateService(monitor, factory, gatewayMonitor);

        await service.PollStreamBatchAsync(poller, fencingToken: 1);

        capturedOptions.Count.ShouldBe(1);
        capturedOptions[0].ConnectionString.ShouldBe("Host=ro-replica;Database=crm;");

        var missingPoller = ValidPoller("write_only_missing");
        await Should.ThrowAsync<InvalidOperationException>(() => service.PollStreamBatchAsync(missingPoller, fencingToken: 1));
    }

    [Fact]
    public async Task Oracle_sets_read_only_transaction_before_query()
    {
        var poller = ValidPoller("ora_src", "SELECT id, updated_at FROM customers WHERE id > @p_wm_id");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gateway = CreateGatewayMonitor("ora_src", "Oracle", "Data Source=ora-prod:1521/ORCL;");

        var conn = new TrackingDbConnection("Data Source=ora-prod:1521/ORCL;");
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("ora_src", Arg.Any<CancellationToken>()).Returns(conn);

        using var service = CreateService(monitor, factory, gateway);
        await service.PollStreamBatchAsync(poller, fencingToken: 1);

        conn.Transactions.Count.ShouldBe(1);
        conn.ExecutedCommands.Count.ShouldBe(2);
        conn.ExecutedCommands[0].ShouldBe("SET TRANSACTION READ ONLY");
        conn.ExecutedCommands[1].ShouldContain("SELECT id, updated_at FROM customers WHERE id > @p_wm_id");
        conn.CommandTransactions[0].ShouldBeSameAs(conn.Transactions[0]);
        conn.CommandTransactions[1].ShouldBeSameAs(conn.Transactions[0]);
        conn.Transactions[0].RollbackCount.ShouldBe(1);
        conn.Transactions[0].CommitCount.ShouldBe(0);
    }

    [Fact]
    public async Task SqlServer_rolls_back_never_commits()
    {
        var poller = ValidPoller("sqlsrv", "SELECT id, updated_at FROM customers WHERE id > @p_wm_id");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gateway = CreateGatewayMonitor("sqlsrv", "SqlServer", "Server=sql-ro;Database=crm;ApplicationIntent=ReadOnly;");

        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1L, ["updated_at"] = "2026-11-14T10:00:00Z" }
        };
        var conn = new TrackingDbConnection(gateway.CurrentValue.DataSources.Connections["sqlsrv"].ConnectionString, rows);
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("sqlsrv", Arg.Any<CancellationToken>()).Returns(conn);

        using var service = CreateService(monitor, factory, gateway);
        var published = await service.PollStreamBatchAsync(poller, fencingToken: 1);

        published.ShouldBe(1);
        conn.Transactions.Count.ShouldBe(1);
        conn.CommandTransactions.ShouldAllBe(t => ReferenceEquals(t, conn.Transactions[0]));
        conn.Transactions[0].RollbackCount.ShouldBe(1);
        conn.Transactions[0].CommitCount.ShouldBe(0);
    }

    [Fact]
    public void Unsupported_provider_and_tenant_mismatch_fail_options_validation()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:CustomQueryPoller:Enabled"] = "true"
            })
            .Build();

        services.AddSingleton(Substitute.For<ISqlConnectionFactory>());
        services.AddSingleton(Substitute.For<ICdcStreamStore>());
        services.AddSingleton(Substitute.For<ICdcStreamSignal>());
        services.AddSingleton(Substitute.For<IDistributedClusterStateProvider>());
        services.AddSingleton<ISqlEngine>(FastSqlEngine.Default);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(new TableIdentifier("crm", "dbo", "customers"), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = new TableIdentifier("crm", "dbo", "customers"),
                OwnerTenantId = "tenant-a"
            });
        services.AddSingleton(metadataRepo);

        var gateway = new GatewayOptions();
        gateway.DataSources.Connections["db_bricks"] = new DataSourceConnectionOptions { Provider = "Databricks", ConnectionString = "Host=adb;" };
        gateway.DataSources.Connections["pg_ok"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=pg;" };
        var gwMonitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        gwMonitor.CurrentValue.Returns(gateway);
        services.AddSingleton(gwMonitor);
        services.AddSingleton(Options.Create(gateway));

        services.AddCustomQueryPoller(config);
        using var sp = services.BuildServiceProvider();

        var optionsValidator = sp.GetRequiredService<IValidateOptions<CustomQueryPollerOptions>>();

        // 1. Databricks provider rejected at startup options validation
        var unsupportedResult = optionsValidator.Validate(
            Options.DefaultName,
            new CustomQueryPollerOptions { Enabled = true, Pollers = [ValidPoller("db_bricks")] });
        unsupportedResult.Failed.ShouldBeTrue();

        // 2. OwnerTenantId mismatch against catalog TableMetadata.OwnerTenantId rejected
        var tenantMismatchPoller = ValidPoller("pg_ok");
        tenantMismatchPoller.OwnerTenantId = "tenant-b";
        var mismatchResult = optionsValidator.Validate(
            Options.DefaultName,
            new CustomQueryPollerOptions { Enabled = true, Pollers = [tenantMismatchPoller] });
        mismatchResult.Failed.ShouldBeTrue();

        // 3. Valid PostgreSql poller with matching OwnerTenantId passes
        var okResult = optionsValidator.Validate(
            Options.DefaultName,
            new CustomQueryPollerOptions { Enabled = true, Pollers = [ValidPoller("pg_ok")] });
        okResult.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Poll_error_log_omits_exception_message_and_includes_sql_state()
    {
        var poller = ValidPoller("pg_src");
        var monitor = new MutableOptionsMonitor<CustomQueryPollerOptions>(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });
        var gateway = CreateGatewayMonitor("pg_src", "PostgreSql", "Host=pg-ro;Database=crm;");

        var secretMarker = "SECRET_SQL_FRAGMENT_AND_PASSWORD_DO_NOT_LOG";
        var conn = new ThrowingDbConnection(new FakeSqlDbException($"syntax error near '{secretMarker}'", "42601"));
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync("pg_src", Arg.Any<CancellationToken>()).Returns(conn);

        var logger = new CapturingLogger<CustomQueryPollerService>();
        using var service = CreateService(monitor, factory, gateway, logger: logger);

        await Should.ThrowAsync<FakeSqlDbException>(() => service.PollStreamBatchAsync(poller, fencingToken: 1));

        logger.Entries.ShouldNotBeEmpty();
        var errorEntry = logger.Entries.Where(e => e.Level == LogLevel.Error).ShouldHaveSingleItem();
        errorEntry.Message.ShouldContain("crm_customers");
        errorEntry.Message.ShouldContain(nameof(FakeSqlDbException));
        errorEntry.Message.ShouldContain("42601");
        errorEntry.Message.ShouldNotContain(secretMarker);
        errorEntry.Exception.ShouldBeNull();
    }

    private sealed class TestablePollerService(
        IOptionsMonitor<CustomQueryPollerOptions> options,
        ICdcStreamStore streamStore,
        CdcChangeEventBuilder eventBuilder,
        IDataSourceConnectionFactory factory,
        ICdcStreamSignal signal,
        IDistributedClusterStateProvider clusterState,
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        CustomQueryPollerValidator validator)
        : CustomQueryPollerService(
            options,
            streamStore,
            eventBuilder,
            factory,
            signal,
            clusterState,
            NullLogger<CustomQueryPollerService>.Instance,
            gatewayOptions: gatewayOptions,
            validator: validator)
    {
        public Task ExecuteConfiguredPartitionAsync(string streamName, long fencingToken)
        {
            var partition = new Autheris.Application.Runtime.Hosting.ClusterPartition(
                $"cdc:query:poll:{streamName.ToLowerInvariant()}",
                new Autheris.Application.Runtime.Hosting.ClusterSchedule(true, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.Zero));
            var context = new Autheris.Application.Runtime.Hosting.ClusterJobContext(fencingToken, Substitute.For<IDistributedLease>());
            return ExecutePartitionAsync(partition, context, CancellationToken.None);
        }
    }

    private sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _current = initial;
        private Action<T, string?>? _listeners;

        public T CurrentValue => _current;
        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners += listener;
            return new ActionDisposable(() => _listeners -= listener);
        }

        public void Update(T next)
        {
            _current = next;
            _listeners?.Invoke(next, Options.DefaultName);
        }

        private sealed class ActionDisposable(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class SpySqlEngine(ISqlEngine inner) : ISqlEngine
    {
        public int ParseCalls { get; private set; }

        public TrinoSqlEngine.Ast.Emit.CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken)
            => inner.Compile(sql, request, cancellationToken);

        public (TrinoSqlEngine.SqlBaseParser.SingleStatementContext Tree, Antlr4.Runtime.CommonTokenStream Tokens) Parse(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? securityOptions = null,
            CancellationToken cancellationToken = default)
        {
            ParseCalls++;
            return inner.Parse(sql, securityOptions, cancellationToken);
        }

        public (TrinoSqlEngine.SqlBaseParser.StandaloneExpressionContext Tree, Antlr4.Runtime.CommonTokenStream Tokens) ParseExpression(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => inner.ParseExpression(sql, tokenOptions, cancellationToken);

        public TrinoSqlEngine.Analysis.SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
            => inner.Analyze(sql);

        public TrinoSqlEngine.Analysis.SqlQueryMetadata Analyze(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? securityOptions,
            CancellationToken cancellationToken = default)
            => inner.Analyze(sql, securityOptions, cancellationToken);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class FakeSqlDbException(string message, string? sqlState) : DbException(message)
    {
        public override string? SqlState => sqlState;
    }

    private sealed class PostgresLookingTrackingConnection : DbConnection
    {
        private readonly TrackingDbConnection _inner;
        public PostgresLookingTrackingConnection(string connectionString) => _inner = new TrackingDbConnection(connectionString);
        public List<string> ExecutedCommands => _inner.ExecutedCommands;
        public List<TrackingTransaction> Transactions => _inner.Transactions;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public override string Database => "postgres";
        public override string DataSource => "postgres-db";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            var tx = new TrackingTransaction(this);
            _inner.Transactions.Add(tx);
            return tx;
        }
        protected override DbCommand CreateDbCommand() => new TrackingCommand(this, _inner);
    }

    private sealed class TrackingDbConnection(string connectionString, List<Dictionary<string, object?>>? rows = null) : DbConnection
    {
        public List<string> ExecutedCommands { get; } = [];
        public List<TrackingTransaction> Transactions { get; } = [];
        public List<DbTransaction?> CommandTransactions { get; } = [];
        public List<Dictionary<string, object?>> Rows { get; } = rows ?? [];

        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = connectionString;
        public override string Database => "db";
        public override string DataSource => "src";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            var tx = new TrackingTransaction(this);
            Transactions.Add(tx);
            return tx;
        }
        protected override DbCommand CreateDbCommand() => new TrackingCommand(this, this);
    }

    private sealed class TrackingCommand(DbConnection conn, TrackingDbConnection recorder) : DbCommand
    {
        private readonly TrackingParamCollection _params = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            recorder.ExecutedCommands.Add(CommandText);
            recorder.CommandTransactions.Add(DbTransaction);
            return 0;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new TrackingParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            recorder.ExecutedCommands.Add(CommandText);
            recorder.CommandTransactions.Add(DbTransaction);
            return new TrackingDataReader(recorder.Rows);
        }
    }

    private sealed class ThrowingDbConnection(DbException toThrow) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "db";
        public override string DataSource => "src";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new TrackingTransaction(this);
        protected override DbCommand CreateDbCommand() => new ThrowingCommand(this, toThrow);
    }

    private sealed class ThrowingCommand(DbConnection conn, DbException toThrow) : DbCommand
    {
        private readonly TrackingParamCollection _params = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new TrackingParam();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw toThrow;
    }

    private sealed class TrackingTransaction(DbConnection conn) : DbTransaction
    {
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() => CommitCount++;
        public override Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
        public override void Rollback() => RollbackCount++;
        public override Task RollbackAsync(CancellationToken cancellationToken = default) { Rollback(); return Task.CompletedTask; }
    }

    private sealed class TrackingParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class TrackingParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var idx = IndexOf(parameterName); if (idx >= 0) _items.RemoveAt(idx); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items.Find(p => p.ParameterName == parameterName)!;
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class TrackingDataReader(List<Dictionary<string, object?>> rows) : DbDataReader
    {
        private int _index = -1;
        private readonly List<string> _cols = rows.Count > 0 ? [.. rows[0].Keys] : [];
        public override int FieldCount => _cols.Count;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => rows[_index][name] ?? DBNull.Value;
        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override string GetName(int ordinal) => _cols[ordinal];
        public override int GetOrdinal(string name) => _cols.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        public override bool IsDBNull(int ordinal) => rows[_index][_cols[ordinal]] is null;
        public override object GetValue(int ordinal) => rows[_index][_cols[ordinal]] ?? DBNull.Value;
        public override int GetValues(object[] values) { for (var i = 0; i < _cols.Count; i++) values[i] = GetValue(i); return _cols.Count; }
        public override bool NextResult() => false;
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "object";
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override Type GetFieldType(int ordinal) => GetValue(ordinal)?.GetType() ?? typeof(object);
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal)) ?? string.Empty;
        public override System.Collections.IEnumerator GetEnumerator() => rows.GetEnumerator();
    }
}
