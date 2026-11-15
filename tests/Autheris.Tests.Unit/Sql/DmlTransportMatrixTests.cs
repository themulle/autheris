namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed class DmlTransportMatrixTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-301");
    private static readonly TableIdentifier OrdersId = new("default", "main", "orders");
    private static readonly TableIdentifier ExtOrdersId = new("other_src", "main", "ext_orders");

    [Theory]
    [InlineData(SqlTransports.Trino)]
    [InlineData(SqlTransports.Rest)]
    [InlineData(SqlTransports.Mcp)]
    [InlineData(SqlTransports.ArrowExport)]
    [InlineData("FlightSql")]
    [InlineData(SqlTransports.SqlEndpoints)]
    [InlineData(SqlTransports.AsyncJob)]
    [InlineData(SqlTransports.Olap)]
    [InlineData(null)]
    public async Task Dml_IsRejected_OnTransportWithoutWriteWiring(string? transport)
    {
        var harness = CreateHarness(allowDml: true, WithInsertGrant());
        var sql = "INSERT INTO orders (id, tenant_id, amount) VALUES (10, 'tenant-a', 42)";

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            harness.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(sql, Transport: transport),
                CreateUser(),
                Tenant));

        ex.Code.ShouldBe("dml_transport_not_wired");
        harness.Audit.Entries.ShouldContain(e =>
            e.EventType == AuditEventTypes.WebSqlDmlRejected &&
            e.Decision == "DENY");
        harness.Connections.ExecutedCommands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dml_IsAccepted_OnWebSqlWithGrant()
    {
        var harness = CreateHarness(allowDml: true, WithInsertGrant());
        var sql = "INSERT INTO orders (id, tenant_id, amount) VALUES (10, 'tenant-a', 42)";

        await harness.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);

        harness.Connections.ExecutedCommands.Count.ShouldBe(1);
        harness.Connections.CommittedTransactions.ShouldBe(1);
        harness.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.WebSqlDmlExecuted);
    }

    [Fact]
    public async Task Merge_IsRejected_AndAudited()
    {
        var harness = CreateHarness(allowDml: true, WithInsertGrant());
        var sql = """
                  MERGE INTO orders AS t
                  USING (SELECT 1 AS id, 99 AS amount) AS s
                  ON t.id = s.id
                  WHEN MATCHED THEN UPDATE SET amount = s.amount
                  """;

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            harness.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.StatementNotPermitted);
        harness.Audit.Entries.ShouldContain(e =>
            e.EventType == AuditEventTypes.WebSqlDmlRejected &&
            e.Decision == "DENY");
        harness.Connections.ExecutedCommands.ShouldBeEmpty();
    }

    [Fact]
    public async Task FederatedDml_IsRejected()
    {
        var harness = CreateHarness(allowDml: true, WithInsertGrant(), includeExternalSource: true);
        var sql = "INSERT INTO default.main.orders (id, tenant_id, amount) SELECT id, tenant_id, amount FROM other_src.main.ext_orders";

        await Should.ThrowAsync<Exception>(() =>
            harness.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));

        harness.Connections.ExecutedCommands.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AccessTransport.Trino)]
    [InlineData(AccessTransport.Rest)]
    [InlineData(AccessTransport.Mcp)]
    [InlineData(AccessTransport.ArrowExport)]
    [InlineData(AccessTransport.Olap)]
    [InlineData(AccessTransport.GraphQl)]
    [InlineData(AccessTransport.OData)]
    [InlineData(AccessTransport.Federation)]
    [InlineData(AccessTransport.Streaming)]
    public async Task AccessDecisionPoint_NonWebSqlTransport_IsDenied(AccessTransport transport)
    {
        var harness = CreateHarness(allowDml: true, WithInsertGrant());
        var secContext = SecurityPrincipalContext.FromPrincipal(CreateUser(), Tenant, clientIp: IPAddress.Loopback);
        var decision = await harness.Pdp.DecideAsync(
            new AccessRequest(
                secContext,
                harness.OrdersMetadata,
                AccessOperation.Insert,
                transport,
                Columns: ["id", "tenant_id", "amount"]),
            CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Code == "WRITE_TRANSPORT_NOT_WIRED");
    }

    private static Consent WithInsertGrant() => new()
    {
        Id = Guid.NewGuid(),
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
        ValidTo = DateTimeOffset.UtcNow.AddHours(1),
        WriteRules =
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "amount"], WriteRowScope.OwnRows)
        ]
    };

    private static ClaimsPrincipal CreateUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, UserSid.Value),
            new("tenant_id", Tenant.Value),
            new("scp", "Sql.Write"),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static Harness CreateHarness(bool allowDml, Consent consent, bool includeExternalSource = false)
    {
        var ordersMeta = new TableMetadata
        {
            Identifier = OrdersId,
            PrimaryKeyColumns = ["id"],
            Table = new Table
            {
                SourceName = string.Empty,
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

        var metas = new List<TableMetadata> { ordersMeta };
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new DataSourceConnectionOptions { ConnectionString = "Data Source=stub", Provider = "Sqlite" }
        };

        if (includeExternalSource)
        {
            metas.Add(new TableMetadata
            {
                Identifier = ExtOrdersId,
                PrimaryKeyColumns = ["id"],
                Table = new Table
                {
                    SourceName = "other_src",
                    SourceType = "Sqlite",
                    SchemaName = "main",
                    TableName = "ext_orders",
                    IsActive = true
                },
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                    new TableColumn { ColumnName = "amount", DataType = "integer" }
                ]
            });
            connections["other_src"] = new DataSourceConnectionOptions { ConnectionString = "Data Source=stub2", Provider = "Sqlite" };
        }

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = allowDml, MaxAffectedRows = 100 },
            DataSources = new SqlDataSourceOptions { Connections = connections }
        };
        var options = Options.Create(gatewayOptions);

        var tableRepo = new FakeMetadataRepo(metas);
        var consentRepo = new FakeConsentRepo(consent);
        var auditRepo = new FakeAuditRepo();
        var connFactory = new FakeSqlConnectionFactory();
        var resolution = new ConsentResolutionService();
        var evaluator = new WriteGrantEvaluator(consentRepo);
        var typedPolicies = GovernedTestSupport.TypedPolicies(options);
        var pdp = new AccessDecisionPoint(
            options,
            resolution,
            consentRepo,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            typedPolicies: typedPolicies,
            writeGrantEvaluator: evaluator);

        var service = GovernedSqlExecutionServiceFactory.Create(
            options: options,
            consentRepository: consentRepo,
            tableRepository: tableRepo,
            connectionFactory: connFactory,
            auditLogRepository: auditRepo,
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            sqlEngine: new FastSqlEngine(),
            policyFactory: GovernedTestSupport.PolicyFactory(tableRepo, options),
            typedPolicies: typedPolicies,
            accessDecisionPoint: pdp);

        return new Harness(service, auditRepo, connFactory, pdp, ordersMeta);
    }

    private sealed class Harness(
        GovernedSqlExecutionService service,
        FakeAuditRepo audit,
        FakeSqlConnectionFactory connections,
        AccessDecisionPoint pdp,
        TableMetadata ordersMetadata)
    {
        public GovernedSqlExecutionService Service { get; } = service;
        public FakeAuditRepo Audit { get; } = audit;
        public FakeSqlConnectionFactory Connections { get; } = connections;
        public AccessDecisionPoint Pdp { get; } = pdp;
        public TableMetadata OrdersMetadata { get; } = ordersMetadata;
    }

    private sealed class FakeMetadataRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FakeAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeConsentRepo(params Consent[] consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.ToHashSet();
            IReadOnlyList<Consent> hits = consents
                .Where(c => c.TableIdentifier.Equals(table) && (tenantId == null || c.TenantId == tenantId.Value) && c.IsActive(atTime) && c.GranteeSid.HasValue && set.Contains(c.GranteeSid.Value))
                .ToList();
            return Task.FromResult(hits);
        }
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
    {
        public List<(string Sql, IReadOnlyDictionary<string, object?> Params)> ExecutedCommands { get; } = [];
        public int CommittedTransactions { get; private set; }
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
            Task.FromResult<DbConnection>(new FakeConnection(this));

        private sealed class FakeConnection(FakeSqlConnectionFactory owner) : DbConnection
        {
            private ConnectionState _state = ConnectionState.Open;
            [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
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

        private sealed class FakeTx(FakeConnection conn, FakeSqlConnectionFactory owner) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => conn;
            public override void Commit() => owner.CommittedTransactions++;
            public override Task CommitAsync(CancellationToken cancellationToken = default) { owner.CommittedTransactions++; return Task.CompletedTask; }
            public override void Rollback() { }
        }

        private sealed class FakeCmd(FakeConnection conn, FakeSqlConnectionFactory owner) : DbCommand
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
            public override int ExecuteNonQuery() { owner.ExecutedCommands.Add((CommandText, _params.ToDict())); return 1; }
            public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());
            public override object? ExecuteScalar() => null;
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new DataTable().CreateDataReader();
        }

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
