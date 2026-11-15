namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedSqlWriteExecutionSecurityTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string SubjectA = "S-1-5-21-USER-A";
    private const string SubjectB = "S-1-5-21-USER-B";
    private const string ReadConn = "Host=read-replica.internal;Database=app;Username=reader";
    private const string WriteConn = "Host=primary-writer.internal;Database=app;Username=writer";
    private static readonly TableIdentifier OrdersTable = new("default", "main", "orders");

    public static IEnumerable<object[]> BudgetStores()
    {
        var t1 = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        yield return [new InMemoryWriteBudgetStore(t1), t1];

        var t2 = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        yield return [new DistributedCacheWriteBudgetStore(cache, t2), t2];
    }

    [Theory]
    [MemberData(nameof(BudgetStores))]
    public async Task WriteBudgetStore_EnforcesStatementAndRowLimits_WithTenantAndCallerIsolation_AndWindowReset(
        IWriteBudgetStore store,
        object clockObj)
    {
        var clock = (ManualTimeProvider)clockObj;
        var limit = new WriteBudgetLimit(StatementsPerMinute: 2, RowsPerHour: 10, GrantMaxRowsPerStatement: 4, ConfiguredMaxAffectedRows: 100);
        limit.EffectiveStatementMaxRows().ShouldBe(4);

        var tA = new TenantId(TenantA);
        var tB = new TenantId(TenantB);

        (await store.TryAcquireStatementAsync(tA, SubjectA, limit)).Allowed.ShouldBeTrue();
        (await store.TryRecordAffectedRowsAsync(tA, SubjectA, 6, limit.RowsPerHour)).ShouldBeTrue();

        (await store.TryAcquireStatementAsync(tA, SubjectA, limit)).Allowed.ShouldBeTrue();
        (await store.TryRecordAffectedRowsAsync(tA, SubjectA, 4, limit.RowsPerHour)).ShouldBeTrue();

        // Third statement in same minute AND hourly rows now exhausted (10/10 used).
        var denied = await store.TryAcquireStatementAsync(tA, SubjectA, limit);
        denied.Allowed.ShouldBeFalse();

        // Different caller in same tenant and different tenant keep independent budgets.
        (await store.TryAcquireStatementAsync(tA, SubjectB, limit)).Allowed.ShouldBeTrue();
        (await store.TryAcquireStatementAsync(tB, SubjectA, limit)).Allowed.ShouldBeTrue();

        // Advancing by 1 minute does NOT clear hourly row exhaustion, but advancing by 1 hour restores both.
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.TryAcquireStatementAsync(tA, SubjectA, limit)).Allowed.ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(1));
        (await store.TryAcquireStatementAsync(tA, SubjectA, limit)).Allowed.ShouldBeTrue();
        (await store.TryRecordAffectedRowsAsync(tA, SubjectA, 11, limit.RowsPerHour)).ShouldBeFalse();
    }

    [Fact]
    public async Task Dml_UsesWriteConnectionString_SelectNeverUsesWriteConnectionString_AndMissingWriteConnectionFailsClosed()
    {
        var hWithWrite = CreateHarness(readConn: ReadConn, writeConn: WriteConn);
        await hWithWrite.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT amount FROM orders WHERE id = 1", Transport: SqlTransports.WebSql),
            Caller(SubjectA, TenantA),
            new TenantId(TenantA));

        hWithWrite.Connections.OpenedConnectionStrings.ShouldBe([ReadConn]);

        await hWithWrite.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 5 WHERE id = 1", Transport: SqlTransports.WebSql),
            Caller(SubjectA, TenantA),
            new TenantId(TenantA));

        hWithWrite.Connections.OpenedConnectionStrings.ShouldBe([ReadConn, WriteConn]);
        hWithWrite.Connections.CommittedTransactions.ShouldBe(1);

        var hMissingWrite = CreateHarness(readConn: ReadConn, writeConn: null);
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => hMissingWrite.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 5 WHERE id = 1", Transport: SqlTransports.WebSql),
            Caller(SubjectA, TenantA),
            new TenantId(TenantA)));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.DmlWriteConnectionMissing);
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(403);
        mapped.Code.ShouldBe(GovernedSqlErrorCodes.DmlWriteConnectionMissing);
        hMissingWrite.Connections.OpenedConnectionStrings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExceedingRowBudgetInBeforeCommit_Throws429AndRollsBackTransaction()
    {
        var budget = new InMemoryWriteBudgetStore();
        var h = CreateHarness(
            readConn: ReadConn,
            writeConn: WriteConn,
            writeBudgetStore: budget,
            writeRowsPerHour: 3,
            onExecuteNonQuery: (_, _) => 5);

        var ex = await Should.ThrowAsync<WriteBudgetExhaustedException>(() => h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 10 WHERE id = 1", Transport: SqlTransports.WebSql),
            Caller(SubjectA, TenantA),
            new TenantId(TenantA)));

        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(429);
        mapped.Code.ShouldBe(GovernedSqlErrorCodes.WriteBudgetExhausted);
        h.Connections.CommittedTransactions.ShouldBe(0);
        h.Connections.RolledBackTransactions.ShouldBe(1);
    }

    [Fact]
    public async Task OutcomeAuditFailureInBeforeCommit_ThrowsAuditUnavailableAndRollsBackTransaction()
    {
        var h = CreateHarness(
            readConn: ReadConn,
            writeConn: WriteConn,
            throwOnAuditEventType: AuditEventTypes.WebSqlDmlExecuted);

        await Should.ThrowAsync<AuditUnavailableException>(() => h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 10 WHERE id = 1", Transport: SqlTransports.WebSql),
            Caller(SubjectA, TenantA),
            new TenantId(TenantA)));

        h.Connections.CommittedTransactions.ShouldBe(0);
        h.Connections.RolledBackTransactions.ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_MissingKeyReturns400_SameSqlReplaysOutcomeWithoutReExecution_DifferentPayloadReturns409()
    {
        var store = new InMemoryIdempotencyStore();
        var exec = new RecordingSqlService();
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = true, WriteIdempotencyTtlSeconds = 3600 }
        });

        var missingCtx = BuildHttpContext("{\"sql\":\"UPDATE orders SET amount = 1 WHERE id = 1\"}", store, key: null);
        await WebSqlEndpoints.HandleWebSqlRequest(missingCtx, exec, options, NullLoggerFactory.Instance);
        missingCtx.Response.StatusCode.ShouldBe(400);
        exec.Calls.ShouldBe(0);

        const string firstPayload = "{\"sql\":\"UPDATE orders SET amount = 1 WHERE id = 1\",\"parameters\":{\"p\":1}}";
        var firstCtx = BuildHttpContext(firstPayload, store, key: "idem-1");
        await WebSqlEndpoints.HandleWebSqlRequest(firstCtx, exec, options, NullLoggerFactory.Instance);
        firstCtx.Response.StatusCode.ShouldBe(200);
        exec.Calls.ShouldBe(1);
        string firstBody = ReadResponseBody(firstCtx);

        var replayCtx = BuildHttpContext(firstPayload, store, key: "idem-1");
        await WebSqlEndpoints.HandleWebSqlRequest(replayCtx, exec, options, NullLoggerFactory.Instance);
        replayCtx.Response.StatusCode.ShouldBe(200);
        replayCtx.Response.Headers["X-Idempotency-Replayed"].ToString().ShouldBe("true");
        ReadResponseBody(replayCtx).ShouldBe(firstBody);
        exec.Calls.ShouldBe(1);

        const string differentSqlPayload = "{\"sql\":\"UPDATE orders SET amount = 99 WHERE id = 1\",\"parameters\":{\"p\":1}}";
        var conflictCtx = BuildHttpContext(differentSqlPayload, store, key: "idem-1");
        await WebSqlEndpoints.HandleWebSqlRequest(conflictCtx, exec, options, NullLoggerFactory.Instance);
        conflictCtx.Response.StatusCode.ShouldBe(409);
        exec.Calls.ShouldBe(1);

        const string differentParamPayload = "{\"sql\":\"UPDATE orders SET amount = 1 WHERE id = 1\",\"parameters\":{\"p\":2}}";
        var paramConflictCtx = BuildHttpContext(differentParamPayload, store, key: "idem-1");
        await WebSqlEndpoints.HandleWebSqlRequest(paramConflictCtx, exec, options, NullLoggerFactory.Instance);
        paramConflictCtx.Response.StatusCode.ShouldBe(409);
        exec.Calls.ShouldBe(1);
    }

    private static DefaultHttpContext BuildHttpContext(string jsonBody, IIdempotencyStore store, string? key)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/v1/sql";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        if (key is not null)
        {
            ctx.Request.Headers["Idempotency-Key"] = key;
        }

        ctx.User = Caller(SubjectA, TenantA);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadResponseBody(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
    }

    private static ClaimsPrincipal Caller(string sid, string tenantId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("sub", sid),
                new Claim("tenant_id", tenantId),
                new Claim("scp", "Sql.Write"),
                GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
                GroupResolutionClaims.CreateMarker()
            ],
            "Test"));

    private static (GovernedSqlExecutionService Service, RecordingConnectionFactory Connections) CreateHarness(
        string readConn,
        string? writeConn,
        IWriteBudgetStore? writeBudgetStore = null,
        long writeRowsPerHour = 10_000,
        Func<string, IReadOnlyDictionary<string, object?>, int>? onExecuteNonQuery = null,
        string? throwOnAuditEventType = null)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new TableMetadata
        {
            Identifier = OrdersTable,
            PrimaryKeyColumns = ["id"],
            Table = new Table
            {
                SourceName = "default",
                SourceType = "Sqlite",
                SchemaName = "main",
                TableName = "orders",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "integer" }
            ]
        };

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId(TenantA),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(SubjectA),
            TableIdentifier = OrdersTable,
            Effect = ConsentEffect.Allow,
            ValidFrom = now.AddMinutes(-5),
            ValidTo = now.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "tenant_id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
            ],
            WriteRules =
            [
                new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.AllRows, MaxRowsPerStatement: 50)
            ]
        };

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowDml = true,
                MaxAffectedRows = 100,
                WriteStatementsPerMinute = 60,
                WriteRowsPerHour = writeRowsPerHour,
                WriteIdempotencyTtlSeconds = 3600
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions
                    {
                        Provider = "Sqlite",
                        ConnectionString = readConn,
                        WriteConnectionString = writeConn
                    }
                }
            }
        };

        var opts = Options.Create(gatewayOptions);
        var tableRepo = new FakeMetadataRepo(metadata);
        var consentRepo = new FakeConsentRepo(consent);
        var auditRepo = new FakeAuditRepo(throwOnAuditEventType);
        var connFactory = new RecordingConnectionFactory(onExecuteNonQuery);
        var resolution = new ConsentResolutionService();
        var evaluator = new WriteGrantEvaluator(consentRepo);
        var typedPolicies = GovernedTestSupport.TypedPolicies(opts);
        var pdp = new AccessDecisionPoint(
            opts,
            resolution,
            consentRepo,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            typedPolicies: typedPolicies,
            writeGrantEvaluator: evaluator);

        var service = Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            opts,
            connectionFactory: connFactory,
            auditLogRepository: auditRepo,
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: tableRepo,
            consentRepository: consentRepo,
            sqlEngine: new FastSqlEngine(),
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, opts),
            typedPolicies: typedPolicies,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            accessDecisionPoint: pdp,
            writeBudgetStore: writeBudgetStore);

        return (service, connFactory);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utc = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan delta) => _utc = _utc.Add(delta);
    }

    private sealed class RecordingSqlService : IGovernedSqlExecutionService
    {
        public int Calls { get; private set; }
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new GovernedSqlResult(request.Sql, [], [], 0, 0));
        }
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMetadataRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier t, CancellationToken ct = default) => Task.FromResult(t.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier t, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FakeAuditRepo(string? throwOnEventType) : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (throwOnEventType is not null && entry.EventType == throwOnEventType)
            {
                throw new InvalidOperationException("Audit store unavailable");
            }
            return Task.CompletedTask;
        }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) => RecordAuditEventAsync(entry, ct);
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeConsentRepo(params Consent[] consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.ToHashSet();
            IReadOnlyList<Consent> hits = consents.Where(c => c.TableIdentifier.Equals(table) && (tenantId == null || c.TenantId == tenantId.Value) && c.IsActive(atTime) && c.GranteeSid.HasValue && set.Contains(c.GranteeSid.Value)).ToList();
            return Task.FromResult(hits);
        }
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class RecordingConnectionFactory(Func<string, IReadOnlyDictionary<string, object?>, int>? onExecuteNonQuery) : ISqlConnectionFactory
    {
        public List<string> OpenedConnectionStrings { get; } = [];
        public int CommittedTransactions { get; private set; }
        public int RolledBackTransactions { get; private set; }

        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            OpenedConnectionStrings.Add(options.ConnectionString);
            return Task.FromResult<DbConnection>(new FakeConnection(this, options.ConnectionString));
        }

        private sealed class FakeConnection(RecordingConnectionFactory owner, string connStr) : DbConnection
        {
            private ConnectionState _state = ConnectionState.Open;
            [AllowNull] public override string ConnectionString { get; set; } = connStr;
            public override string Database => "main";
            public override string DataSource => "stub";
            public override string ServerVersion => "1.0";
            public override ConnectionState State => _state;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() => _state = ConnectionState.Closed;
            public override void Open() => _state = ConnectionState.Open;
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTx(this, owner);
            protected override DbCommand CreateDbCommand() => new FakeCmd(this, owner);
        }

        private sealed class FakeTx(FakeConnection conn, RecordingConnectionFactory owner) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => conn;
            public override void Commit() => owner.CommittedTransactions++;
            public override Task CommitAsync(CancellationToken cancellationToken = default) { owner.CommittedTransactions++; return Task.CompletedTask; }
            public override void Rollback() => owner.RolledBackTransactions++;
            public override Task RollbackAsync(CancellationToken cancellationToken = default) { owner.RolledBackTransactions++; return Task.CompletedTask; }
        }

        private sealed class FakeCmd(FakeConnection conn, RecordingConnectionFactory owner) : DbCommand
        {
            private readonly FakeParams _params = new();
            [AllowNull] public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; } = CommandType.Text;
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = conn;
            protected override DbParameterCollection DbParameterCollection => _params;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new FakeParam();
            public override int ExecuteNonQuery() => owner.OnExecute(CommandText, _params.ToDict());
            public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());
            public override object? ExecuteScalar() => null;
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new DataTable().CreateDataReader();
        }

        private int OnExecute(string sql, IReadOnlyDictionary<string, object?> p) => onExecuteNonQuery?.Invoke(sql, p) ?? 1;

        private sealed class FakeParams : DbParameterCollection
        {
            private readonly List<DbParameter> _list = [];
            public IReadOnlyDictionary<string, object?> ToDict() => _list.ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.OrdinalIgnoreCase);
            public override int Count => _list.Count;
            public override object SyncRoot => _list;
            public override int Add(object value) { _list.Add((DbParameter)value); return _list.Count - 1; }
            public override void AddRange(Array values) { foreach (DbParameter p in values) _list.Add(p); }
            public override void Clear() => _list.Clear();
            public override bool Contains(object value) => _list.Contains((DbParameter)value);
            public override bool Contains(string value) => IndexOf(value) >= 0;
            public override void CopyTo(Array array, int index) => ((ICollection)_list).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _list.GetEnumerator();
            protected override DbParameter GetParameter(int index) => _list[index];
            protected override DbParameter GetParameter(string parameterName) => _list[IndexOf(parameterName)];
            public override int IndexOf(object value) => _list.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) => _list.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
            public override void Insert(int index, object value) => _list.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _list.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _list.RemoveAt(index);
            public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _list.RemoveAt(i); }
            protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _list[i] = value; }
        }

        private sealed class FakeParam : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
            public override bool IsNullable { get; set; }
            [AllowNull] public override string ParameterName { get; set; } = string.Empty;
            public override int Size { get; set; }
            [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
            public override bool SourceColumnNullMapping { get; set; }
            public override object? Value { get; set; }
            public override void ResetDbType() { }
        }
    }
}
