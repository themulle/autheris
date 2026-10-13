namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Databricks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// WP-X1.2 / X1.3: the execution half of the governed pipeline. The tests hand a compiled statement to the executor
/// (<c>ExecuteRewrittenAsync</c>) and observe what reaches the connection: which text, which transaction, which errors and which audit.
/// </summary>
public sealed class GovernedSqlExecutorX1Tests
{
    private const string Tenant = "tenant_a";
    private const string DataSource = "default";

    private sealed class FakeDbException(string message) : DbException(message);

    private static ClaimsPrincipal Writer() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-X1"), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Writer")], "Test"));

    private static TableMetadata Orders(string sourceType)
    {
        string schema = sourceType.ToLowerInvariant() switch { "sqlite" => "main", "sqlserver" => "dbo", _ => "public" };
        return new TableMetadata
        {
            Identifier = new TableIdentifier("default", schema, "orders"),
            Table = new Table { TableName = "orders", SchemaName = schema, SourceName = string.Empty, SourceType = sourceType },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
    }

    private static GatewayOptions Gateway(string provider, long maxAffectedRows = 1000, bool storeCompiledSql = false, string? hmacRef = null, int timeoutSeconds = 30) => new()
    {
        WebSql = new WebSqlOptions { Enabled = true, AllowDml = true, DmlWriterRoles = ["Writer"], DefaultDataSourceName = DataSource, MaxAffectedRows = maxAffectedRows, ExecutionTimeoutSeconds = timeoutSeconds },
        Audit = new AuditOptions { StoreCompiledSql = storeCompiledSql },
        DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = hmacRef ?? string.Empty },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [DataSource] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "Host=unused" }
            }
        }
    };

    /// <summary>Compiles a statement through the real governed pipeline (catalog, policies, engine) for a source type.</summary>
    private static async Task<CompiledSql> CompileAsync(string sourceType, string sql, ClaimsPrincipal? user = null)
    {
        var table = Orders(sourceType);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.Equals(ci.Arg<TableIdentifier>()) ? table : null));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        var service = new GovernedSqlExecutionService(
            Options.Create(Gateway(sourceType == "PostgreSQL" ? "PostgreSql" : sourceType)), executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents);
        return await service.CompileAsync(sql, user ?? Writer(), new TenantId(Tenant));
    }

    private static GovernedRewrite Rewrite(CompiledSql compiled) => new(compiled, [], DataSource);

    private static DmlAuditContext Dml() => new() { StatementType = TrinoSqlEngine.Analysis.SqlStatementType.Insert, Tables = ["orders"] };

    private sealed class Harness
    {
        public required GovernedSqlExecutor Executor { get; init; }
        public required ISqlConnectionFactory Factory { get; init; }
        public required FakeDbConnection Connection { get; init; }
        public required List<AuditLogEntry> Audit { get; init; }
        public required CapturingLogger<GovernedSqlExecutor> Log { get; init; }
    }

    private static Harness CreateHarness(
        string provider,
        Func<FakeDbCommand>? commandFactory = null,
        bool sessionTransaction = false,
        GatewayOptions? options = null,
        IKeyVaultSecretProvider? secrets = null,
        IProviderExecutionGuard? guard = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
    {
        var gateway = options ?? Gateway(provider);
        var connection = new FakeDbConnection(commandFactory);
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DbConnection>(connection));
        var session = Substitute.For<IDbSessionContextInitializer>();
        session.InitializeSessionAsync(Arg.Any<DbConnection>(), Arg.Any<DatabaseDialect>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<DbTransaction?>(sessionTransaction ? ((FakeDbConnection)ci.Arg<DbConnection>()).BeginSessionTransaction() : null));
        var audit = new List<AuditLogEntry>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(audit.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var log = new CapturingLogger<GovernedSqlExecutor>();
        var opts = Options.Create(gateway);
        var executor = new GovernedSqlExecutor(
            new GovernedSqlRewriter(opts), opts, auditRepo, guard ?? PassThroughExecutionGuard.Instance, factory, environment: environment, logger: log, sessionInitializer: session, secretProvider: secrets);
        return new Harness { Executor = executor, Factory = factory, Connection = connection, Audit = audit, Log = log };
    }

    private static Task<string> ExecuteAsync(Harness h, CompiledSql compiled, GovernedSqlQueryRequest? request = null, DmlAuditContext? dml = null,
        IReadOnlyDictionary<string, object?>? clientValues = null, Func<DbDataReader, CancellationToken, Task>? rowWriter = null) =>
        h.Executor.ExecuteRewrittenAsync(
            request ?? new GovernedSqlQueryRequest("SELECT 1"), Writer(), new TenantId(Tenant), Rewrite(compiled), dml ?? new DmlAuditContext(),
            clientValues ?? new Dictionary<string, object?>(), rowWriter ?? ((_, _) => Task.CompletedTask), CancellationToken.None);

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2: DML runs through the checked executor, inside the session transaction
    // -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PostgreSql", "PostgreSQL", true)]    // the session initializer owns a transaction (set_config(..., true) is transaction-local)
    [InlineData("SqlServer", "SqlServer", false)]     // none of these gives a transaction: the executor begins one after the session is initialized
    [InlineData("Oracle", "Oracle", false)]
    [InlineData("Sqlite", "Sqlite", false)]
    public async Task WebSqlDml_RunsThroughCheckedExecutor_InSessionTransaction(string provider, string sourceType, bool sessionGivesTransaction)
    {
        var compiled = await CompileAsync(sourceType, "INSERT INTO orders (id, amount, tenant_id) VALUES (1, 10, 'tenant_a')");
        var h = CreateHarness(provider, () => sourceType == "Oracle" ? new FakeBindByNameCommand() : new FakeDbCommand(), sessionGivesTransaction);

        await ExecuteAsync(h, compiled, dml: Dml());

        var tx = h.Connection.Transactions.ShouldHaveSingleItem();   // never a second (nested) transaction
        tx.Commits.ShouldBe(1);
        tx.Rollbacks.ShouldBe(0);
        tx.LastCommitTokenCanBeCanceled.ShouldBe(false);              // L2: the commit does not take the caller's token
        var command = h.Connection.Commands.ShouldHaveSingleItem();
        command.Transaction.ShouldBeSameAs(tx);
        command.CommandText.ShouldBe(compiled.Sql);                   // the compiled text is the only text that reaches the command
        command.BoundValues.ShouldContain(Tenant);
        if (command is FakeBindByNameCommand oracle)
        {
            oracle.BindByName.ShouldBeTrue();
        }

        h.Audit.ShouldContain(e => e.EventType == "WEBSQL_DML_EXECUTED" && e.DetailsJson.Contains("sql.compiled_digest"));
    }

    [Theory]
    [InlineData("PostgreSql", "PostgreSQL", true)]
    [InlineData("Sqlite", "Sqlite", false)]
    public async Task WebSqlDml_AuditKeyUnavailable_FailsClosedBeforeAnyWrite(string provider, string sourceType, bool sessionGivesTransaction)
    {
        // L-2: the audit digest is first needed after the commit; a vault failure must not leave a committed, unaudited write.
        var compiled = await CompileAsync(sourceType, "UPDATE orders SET amount = 1 WHERE id = 1");
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("vault down"));
        var production = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        production.EnvironmentName.Returns("Production");
        var h = CreateHarness(provider, sessionTransaction: sessionGivesTransaction, options: Gateway(provider, hmacRef: "GQL-HMAC"),
            secrets: secrets, environment: production);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => ExecuteAsync(h, compiled, dml: Dml()));

        ex.Message.ShouldContain("Security critical");
        h.Connection.Commands.ShouldBeEmpty();                         // nothing was executed
        h.Connection.Transactions.ShouldAllBe(t => t.Commits == 0);    // and nothing was committed
        h.Audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task WebSqlDml_AffectedRowsLimit_RollsBackOnce_AndIsAuditedAsRejected()
    {
        var compiled = await CompileAsync("PostgreSQL", "UPDATE orders SET amount = 1 WHERE id > 0");
        var h = CreateHarness("PostgreSql", () => new FakeDbCommand { NonQueryResult = 5 }, sessionTransaction: true,
            options: Gateway("PostgreSql", maxAffectedRows: 2));

        var ex = await Should.ThrowAsync<DmlAffectedRowsLimitException>(() => ExecuteAsync(h, compiled, dml: Dml()));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage("DML_AFFECTED_ROWS_LIMIT"));   // no counts
        var tx = h.Connection.Transactions.ShouldHaveSingleItem();
        tx.Commits.ShouldBe(0);
        tx.Rollbacks.ShouldBe(1);                                     // the checked executor is the single completion owner
        h.Audit.ShouldContain(e => e.EventType == "WEBSQL_DML_REJECTED" && e.DetailsJson.Contains("DML_AFFECTED_ROWS_LIMIT"));
    }

    [Fact]
    public async Task CheckedDmlExecutor_CommitsWithoutTheCallerToken_OnceAllChecksPassed()
    {
        // L2: a client that disconnects between the last check and the commit must not turn a passed check into a rollback race.
        var compiled = await CompileAsync("Sqlite", "UPDATE orders SET amount = 1 WHERE id = 1");
        var connection = new FakeDbConnection();
        var tx = connection.BeginSessionTransaction();
        using var cts = new CancellationTokenSource();

        await CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.Sqlite)
            .ExecuteInTransactionAsync(tx, compiled, new Dictionary<string, object?>(), new CheckedDmlLimits(5, 0), cts.Token);

        tx.Commits.ShouldBe(1);
        tx.LastCommitTokenCanBeCanceled.ShouldBe(false);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2 / SEC-X1-10: the provider execution guard (SQLite interrupts a statement at the timeout)
    // -------------------------------------------------------------------------------------------------------------------

    private sealed class RecordingGuard : IProviderExecutionGuard
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public async Task<T> RunAsync<T>(DbConnection connection, TimeSpan timeout, Func<Task<T>> work, CancellationToken ct)
        {
            Timeouts.Add(timeout);
            return await work();
        }
    }

    [Fact]
    public async Task ReadAndDml_RunUnderTheProviderExecutionGuard_WithTheConfiguredTimeout()
    {
        var read = await CompileAsync("Sqlite", "SELECT id FROM orders");
        var dml = await CompileAsync("Sqlite", "UPDATE orders SET amount = 1 WHERE id = 1");
        var guard = new RecordingGuard();
        var options = Gateway("Sqlite", timeoutSeconds: 7);
        var h = CreateHarness("Sqlite", options: options, guard: guard);

        await ExecuteAsync(h, read);
        await ExecuteAsync(h, dml, dml: Dml());

        guard.Timeouts.ShouldBe([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)]);
    }

    [Fact]
    public async Task AnExpensiveStatement_OnRealSqlite_IsInterruptedWithinTheTimeoutPlusOneSecond_AsATimeout()
    {
        // A three-way cross join of 1500 rows (3.4 billion row combinations): without the guard the statement runs for minutes
        // (SQLite has no server-side timeout; CommandTimeout is only a busy timeout). The guard interrupts it after the 1 s budget.
        const string Sql = "SELECT count(*) FROM orders a, orders b, orders c WHERE a.id + b.id + c.id > 0";
        var compiled = await CompileAsync("Sqlite", Sql);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x1-guard-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var connectionString = $"Data Source={path}";
            await using (var setup = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                await setup.OpenAsync();
                await using var create = setup.CreateCommand();
                create.CommandText = "CREATE TABLE orders (id INTEGER, amount REAL, tenant_id TEXT); " +
                    "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1500) INSERT INTO orders SELECT i, 1, 'tenant_a' FROM n;";
                await create.ExecuteNonQueryAsync();
            }

            var gateway = Gateway("Sqlite", timeoutSeconds: 1);
            var factory = Substitute.For<ISqlConnectionFactory>();
            factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
                .Returns(async _ => { var c = new Microsoft.Data.Sqlite.SqliteConnection(connectionString); await c.OpenAsync(); return (DbConnection)c; });
            var session = Substitute.For<IDbSessionContextInitializer>();
            session.InitializeSessionAsync(Arg.Any<DbConnection>(), Arg.Any<DatabaseDialect>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<DbTransaction?>(null));
            var opts = Options.Create(gateway);
            var executor = new GovernedSqlExecutor(
                new GovernedSqlRewriter(opts), opts, Substitute.For<IAuditLogRepository>(), new Autheris.Infrastructure.Persistence.SqliteProviderExecutionGuard(),
                factory, null, NullLogger.Instance, null, session);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            await Should.ThrowAsync<TimeoutException>(() => executor.ExecuteRewrittenAsync(
                new GovernedSqlQueryRequest(Sql), Writer(), new TenantId(Tenant), Rewrite(compiled), new DmlAuditContext(),
                new Dictionary<string, object?>(), (_, _) => Task.CompletedTask, CancellationToken.None));

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2.5));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.File.Delete(path); } catch { /* best effort */ }
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2: provider errors
    // -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProviderError_Sanitized_OriginalLoggedOnly()
    {
        var compiled = await CompileAsync("Sqlite", "SELECT id FROM orders");
        const string Secret = "duplicate key value (tenant-secret-42)";
        var h = CreateHarness("Sqlite", () => new FakeDbCommand { OnExecuteReader = (_, _) => throw new FakeDbException(Secret) });

        var ex = await Should.ThrowAsync<GovernedSqlException>(() => ExecuteAsync(h, compiled));

        ex.Code.ShouldBe("SQL_PROVIDER_ERROR");
        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage("SQL_PROVIDER_ERROR"));
        ex.InnerException.ShouldBeNull();                              // the inner exception would carry the driver message
        h.Log.Lines.ShouldContain(l => l.Contains(Secret));            // the original stays in the server-side log
    }

    [Fact]
    public async Task ProviderError_Timeout_KeepsItsClassification()
    {
        var compiled = await CompileAsync("Sqlite", "SELECT id FROM orders");
        var h = CreateHarness("Sqlite", () => new FakeDbCommand { OnExecuteReader = (_, _) => throw new TimeoutException("slow") });

        // a timeout is classified before the sanitizer: it is not a DbException, and a DbException that is a timeout is passed through too
        await Should.ThrowAsync<TimeoutException>(() => ExecuteAsync(h, compiled));
    }

    [Fact]
    public async Task ErrorAudit_HasCodeNotMessage()
    {
        var compiled = await CompileAsync("Sqlite", "SELECT id FROM orders");
        const string Secret = "key (tenant-secret-42) already exists";
        var h = CreateHarness("Sqlite", () => new FakeDbCommand { OnExecuteReader = (_, _) => throw new FakeDbException(Secret) });

        await Should.ThrowAsync<GovernedSqlException>(() => ExecuteAsync(h, compiled));

        var error = h.Audit.Single(e => e.EventType == "QUERY_EXECUTION_ERROR");
        error.DetailsJson.ShouldContain("SQL_PROVIDER_ERROR");         // stable code
        error.DetailsJson.ShouldContain(nameof(FakeDbException));      // exception type
        error.DetailsJson.ShouldNotContain("tenant-secret-42");        // never ex.Message
        error.DetailsJson.ShouldNotContain("\"error\"");
    }

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2: dialect agreement, fail closed before any command is created
    // -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Snowflake")]
    [InlineData("duckdb")]
    [InlineData("nonsense")]
    public async Task UnmappedProvider_FailsClosed_NoCommandCreated(string provider)
    {
        var compiled = await CompileAsync("PostgreSQL", "SELECT id FROM orders");
        var h = CreateHarness(provider);

        var ex = await Should.ThrowAsync<GovernedSqlException>(() => ExecuteAsync(h, compiled));

        ex.Code.ShouldBe("SQL_COMPILER_INVARIANT");
        h.Connection.Commands.ShouldBeEmpty();
        h.Connection.Transactions.ShouldBeEmpty();
        await h.Factory.DidNotReceive().CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("PostgreSQL", "Sqlite")]
    [InlineData("Sqlite", "PostgreSql")]
    [InlineData("SqlServer", "Oracle")]
    public async Task BinderDialect_FromConnection_MustEqualCompiledDialect(string compiledFor, string connectionProvider)
    {
        var compiled = await CompileAsync(compiledFor, "SELECT id FROM orders");
        var h = CreateHarness(connectionProvider);

        var ex = await Should.ThrowAsync<GovernedSqlException>(() => ExecuteAsync(h, compiled));

        ex.Code.ShouldBe("SQL_COMPILER_INVARIANT");
        h.Connection.Commands.ShouldBeEmpty();
        await h.Factory.DidNotReceive().CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2: audit (SEC-ADG-23)
    // -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_CompiledDigest_NoValues()
    {
        var master = Encoding.UTF8.GetBytes("master-hmac-secret-0123456789abcdef");
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes("GQL-HMAC").Returns(master);
        var compiled = await CompileAsync("Sqlite", GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT id FROM orders WHERE id = @who", new Dictionary<string, object?> { ["who"] = 1 }, out _));
        var gateway = Gateway("Sqlite", hmacRef: "GQL-HMAC");
        var h = CreateHarness("Sqlite", options: gateway, secrets: secrets);

        await ExecuteAsync(h, compiled,
            new GovernedSqlQueryRequest("SELECT id FROM orders WHERE id = @who"),
            clientValues: new Dictionary<string, object?> { ["who"] = "client-secret-value-77" });

        var entry = h.Audit.Single(e => e.EventType == "WEBSQL_QUERY");
        using var json = System.Text.Json.JsonDocument.Parse(entry.DetailsJson);
        var root = json.RootElement;
        root.GetProperty("compiler_version").GetString().ShouldBe(compiled.CompilerVersion);
        root.GetProperty("dialect").GetString().ShouldBe("Sqlite");
        root.GetProperty("bind_count").GetInt32().ShouldBe(compiled.Parameters.Length);
        string digest = root.GetProperty("sql.compiled_digest").GetString()!;
        digest.ShouldBe(CompiledSqlDigest.Compute(CompiledSqlAuditDigester.DeriveKey(CompiledSqlAuditDigester.DeriveAuditKeyFromMaster(master)), compiled));
        digest.Length.ShouldBe(64);
        entry.DetailsJson.ShouldNotContain("client-secret-value-77");
        entry.DetailsJson.ShouldNotContain(Tenant + "'");              // no value of the statement is part of the record
        root.TryGetProperty("compiledSql", out _).ShouldBeFalse();     // Audit:StoreCompiledSql defaults to false
        root.TryGetProperty("securedSql", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Audit_CompiledSqlText_IsStoredOnlyWhenConfigured()
    {
        var compiled = await CompileAsync("Sqlite", "SELECT id FROM orders");
        var h = CreateHarness("Sqlite", options: Gateway("Sqlite", storeCompiledSql: true));

        await ExecuteAsync(h, compiled);

        using var json = System.Text.Json.JsonDocument.Parse(h.Audit.Single(e => e.EventType == "WEBSQL_QUERY").DetailsJson);
        json.RootElement.GetProperty("compiledSql").GetString().ShouldBe(compiled.Sql);
        json.RootElement.GetProperty("compiledSql").GetString()!.ShouldNotContain(Tenant);   // markers only: no values by construction
    }

    [Fact]
    public void Audit_StoreCompiledSql_DefaultsToFalse() => new AuditOptions().StoreCompiledSql.ShouldBeFalse();

    // -------------------------------------------------------------------------------------------------------------------
    // X1.2: synthetic fallback (G-11, SEC-X1-15)
    // -------------------------------------------------------------------------------------------------------------------

    private static GovernedSqlExecutionService DevelopmentServiceWithoutDatabase(params string[] catalogued)
    {
        var tables = catalogued.Select(t => Orders("Sqlite") with { Identifier = new TableIdentifier("default", "public", t), Table = new Table { TableName = t, SchemaName = "public", SourceType = "Sqlite" } }).ToArray();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(tables.FirstOrDefault(t => t.Identifier.Equals(ci.Arg<TableIdentifier>()))));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return new GovernedSqlExecutionService(
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = DataSource } }), executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            environment: env, logger: NullLogger<GovernedSqlExecutionService>.Instance, consentRepository: consents);
    }

    [Fact]
    public async Task SyntheticFallback_RunsAfterCompile_DeniedQueryStillDenied()
    {
        var service = DevelopmentServiceWithoutDatabase("orders");
        var user = Writer();

        // an allowed statement: the synthetic reader receives the compiled text (markers), never a bound value
        var compiled = await service.CompileAsync("SELECT id FROM orders", user, new TenantId(Tenant));
        var allowed = await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELECT id FROM orders"), user, new TenantId(Tenant));
        allowed.Rows.ShouldHaveSingleItem()["query"].ShouldBe(compiled.Sql);
        allowed.RewrittenSql.ShouldBe(compiled.Sql);
        allowed.Rows[0]["query"]!.ToString()!.ShouldNotContain(Tenant);

        // a denied statement never reaches the synthetic reader: the compile (and the governance before it) comes first
        bool written = false;
        await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("SELECT id FROM not_catalogued"), user, new TenantId(Tenant), (_, _) => { written = true; return Task.CompletedTask; }));
        written.ShouldBeFalse();

        // a statement the compiler rejects is rejected as well
        await Should.ThrowAsync<SecurityException>(() => service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("SELECT load_extension('x') FROM orders"), user, new TenantId(Tenant), (_, _) => { written = true; return Task.CompletedTask; }));
        written.ShouldBeFalse();
    }
}
