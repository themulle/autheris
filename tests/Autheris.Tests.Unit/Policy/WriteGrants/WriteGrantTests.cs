namespace Autheris.Tests.Unit.Policy.WriteGrants;

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
using Autheris.Application.Policy;
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
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed class WriteGrantTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-401");
    private static readonly TableIdentifier OrdersId = new("default", "main", "orders");

    [Fact]
    public async Task Insert_allowed_without_read_grant()
    {
        var writeOnly = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "region", "status", "amount"], WriteRowScope.OwnRows)
        ]);
        var h = CreateHarness(consents: [writeOnly]);

        await h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(
                "INSERT INTO orders (id, tenant_id, region, status, amount) VALUES (10, 'tenant-a', 'EU', 'OPEN', 50)",
                Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);

        h.Connections.ExecutedCommands.Count.ShouldBe(1);
        h.Connections.CommittedTransactions.ShouldBe(1);
    }

    [Fact]
    public async Task Write_grant_never_grants_read()
    {
        var writeOnly = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "region", "status", "amount"], WriteRowScope.AllRows),
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.AllRows)
        ]);
        var h = CreateHarness(consents: [writeOnly]);
        var user = CreateUser();
        var secContext = SecurityPrincipalContext.FromPrincipal(user, Tenant, clientIp: IPAddress.Loopback);

        var readDecision = await h.Pdp.DecideAsync(
            new AccessRequest(secContext, h.Metadata, AccessOperation.Read, AccessTransport.WebSql),
            CancellationToken.None);
        readDecision.IsAllowed.ShouldBeFalse();

        var resolved = h.Resolution.ResolveAccess(UserSid, new HashSet<Sid>(), new HashSet<string>(), OrdersId, [writeOnly], DatabaseDialect.Sqlite);
        resolved.IsAllowed.ShouldBeFalse();

        CatalogVisibility.ComputeAccessStatus(OrdersId, [writeOnly]).ShouldBe("NONE");
        CatalogVisibility.FilterForSubject([h.Metadata], [writeOnly], Tenant).ShouldBeEmpty();

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("SELECT id FROM orders", Transport: SqlTransports.WebSql),
                user,
                Tenant));
    }

    [Fact]
    public async Task Update_own_rows_applies_callers_rls_predicate()
    {
        var readConsent = CreateConsent(
            columnRules:
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
            ],
            rowFilters: [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }]);
        var writeConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.OwnRows)
        ]);
        var h = CreateHarness(consents: [readConsent, writeConsent]);

        await h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 999 WHERE id > 0", Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);

        var (sql, parameters) = h.Connections.ExecutedCommands.ShouldHaveSingleItem();
        sql.ShouldContain("\"region\"");
        sql.ShouldContain("\"tenant_id\"");
        parameters.Values.ShouldContain("EU");
        parameters.Values.ShouldContain("tenant-a");
    }

    [Fact]
    public async Task Update_own_rows_without_read_grant_uses_write_grant_row_filter()
    {
        var writeConsent = CreateConsent(
            writeRules: [new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.OwnRows)],
            rowFilters: [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }]);
        var h = CreateHarness(consents: [writeConsent]);
        var user = CreateUser();

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("SELECT id FROM orders", Transport: SqlTransports.WebSql),
                user,
                Tenant));

        await h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 888 WHERE id > 0", Transport: SqlTransports.WebSql),
            user,
            Tenant);

        var (sql, parameters) = h.Connections.ExecutedCommands.ShouldHaveSingleItem();
        sql.ShouldContain("\"region\"");
        parameters.Values.ShouldContain("EU");
    }

    [Fact]
    public async Task Insert_OwnRows_InListFilter_UsesCheckOption()
    {
        var writeConsent = CreateConsent(
            writeRules: [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "region", "status", "amount"], WriteRowScope.OwnRows)],
            rowFilters: [new ConsentRowFilter { ColumnName = "region", Operator = "IN", ValueType = "string", ValueJson = "[\"EU\",\"UK\"]" }]);
        var calls = 0;
        var h = CreateHarness(
            consents: [writeConsent],
            onExecuteNonQuery: (_, _) => ++calls == 1 ? 1 : 0);

        await h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(
                "INSERT INTO orders (id, tenant_id, region, status, amount) VALUES (20, 'tenant-a', 'EU', 'OPEN', 50)",
                Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);
        var (insertSql, _) = h.Connections.ExecutedCommands[0];
        insertSql.ShouldContain("SELECT");
        insertSql.ShouldContain("WHERE");
        h.Connections.CommittedTransactions.ShouldBe(1);

        await Should.ThrowAsync<DmlCheckOptionViolationException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(
                    "INSERT INTO orders (id, tenant_id, region, status, amount) VALUES (21, 'tenant-a', 'US', 'OPEN', 50)",
                    Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
        h.Connections.CommittedTransactions.ShouldBe(1);
    }

    [Fact]
    public async Task Update_AllRows_keeps_tenant_and_mandatory_filters()
    {
        var readConsent = CreateConsent(
            columnRules:
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
            ],
            rowFilters: [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }]);
        var writeConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.AllRows)
        ]);
        var h = CreateHarness(consents: [readConsent, writeConsent], mandatoryStatusOpen: true);

        await h.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 777 WHERE id > 0", Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);

        var (sql, parameters) = h.Connections.ExecutedCommands.ShouldHaveSingleItem();
        sql.ShouldNotContain("\"region\"");
        sql.ShouldContain("\"tenant_id\"");
        sql.ShouldContain("\"status\"");
        parameters.Values.ShouldContain("tenant-a");
        parameters.Values.ShouldContain("OPEN");
        parameters.Values.ShouldNotContain("EU");
    }

    [Fact]
    public async Task Delete_off_by_default()
    {
        var deleteConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
        ]);
        var hDefault = CreateHarness(consents: [deleteConsent], allowDeleteGrants: false);
        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            hDefault.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("DELETE FROM orders WHERE id = 1", Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
        hDefault.Connections.ExecutedCommands.ShouldBeEmpty();

        var hEnabled = CreateHarness(consents: [deleteConsent], allowDeleteGrants: true);
        await hEnabled.Service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("DELETE FROM orders WHERE id = 1", Transport: SqlTransports.WebSql),
            CreateUser(),
            Tenant);
        hEnabled.Connections.ExecutedCommands.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WriteProtected_Column_And_PrimaryKey_UpdateRejected()
    {
        var writeConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "amount", "secret_note"], WriteRowScope.AllRows),
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "secret_note"], WriteRowScope.AllRows)
        ]);
        var h = CreateHarness(consents: [writeConsent], secretNoteWriteProtected: true);
        var user = CreateUser();

        await Should.ThrowAsync<Exception>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("INSERT INTO orders (id, tenant_id, secret_note) VALUES (30, 'tenant-a', 'x')", Transport: SqlTransports.WebSql),
                user,
                Tenant));
        h.Connections.ExecutedCommands.ShouldBeEmpty();

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("UPDATE orders SET id = 99 WHERE id = 1", Transport: SqlTransports.WebSql),
                user,
                Tenant));
    }

    [Fact]
    public async Task WriteRule_Caps_Statement_Row_Count()
    {
        var writeConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.AllRows, MaxRowsPerStatement: 1)
        ]);
        var h = CreateHarness(
            consents: [writeConsent],
            onExecuteNonQuery: (_, _) => 2);

        await Should.ThrowAsync<DmlAffectedRowsLimitException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("UPDATE orders SET amount = 555 WHERE id > 0", Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
        h.Connections.CommittedTransactions.ShouldBe(0);
    }

    [Fact]
    public async Task Dml_denied_when_AllowDml_false()
    {
        var writeConsent = CreateConsent(writeRules:
        [
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "tenant_id", "region", "status", "amount"], WriteRowScope.AllRows)
        ]);
        var h = CreateHarness(consents: [writeConsent], allowDml: false);

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("INSERT INTO orders (id, tenant_id, region, status, amount) VALUES (50, 'tenant-a', 'EU', 'OPEN', 50)", Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
    }

    [Fact]
    public async Task Dml_denied_without_write_grant()
    {
        var readOnly = CreateConsent(columnRules:
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
        ]);
        var h = CreateHarness(consents: [readOnly]);

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("UPDATE orders SET amount = 1 WHERE id = 1", Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
    }

    [Fact]
    public async Task Dml_denied_when_grant_evaluation_throws()
    {
        var h = CreateHarness(consents: [], consentRepoShouldThrow: true);

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            h.Service.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("UPDATE orders SET amount = 1 WHERE id = 1", Transport: SqlTransports.WebSql),
                CreateUser(),
                Tenant));
    }

    private static Consent CreateConsent(
        IReadOnlyList<WriteRule>? writeRules = null,
        IReadOnlyList<ConsentColumnRule>? columnRules = null,
        IReadOnlyList<ConsentRowFilter>? rowFilters = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = OrdersId,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = now.AddHours(-1),
            ValidTo = now.AddHours(1),
            WriteRules = writeRules ?? [],
            ColumnRules = columnRules ?? [],
            RowFilters = rowFilters ?? []
        };
    }

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

    private static Harness CreateHarness(
        Consent[] consents,
        bool allowDml = true,
        bool allowDeleteGrants = false,
        bool secretNoteWriteProtected = false,
        bool mandatoryStatusOpen = false,
        bool consentRepoShouldThrow = false,
        Func<string, IReadOnlyDictionary<string, object?>, int>? onExecuteNonQuery = null)
    {
        var metadata = new TableMetadata
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
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "integer" },
                new TableColumn { ColumnName = "secret_note", DataType = "varchar", WriteProtected = secretNoteWriteProtected }
            ]
        };

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = allowDml, MaxAffectedRows = 100 },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { ConnectionString = "Data Source=stub", WriteConnectionString = "Data Source=stub", Provider = "Sqlite" }
                }
            }
        };
        var options = Options.Create(gatewayOptions);

        var tableRepo = new FakeMetadataRepo(metadata);
        var consentRepo = new FakeConsentRepo(consents) { ShouldThrow = consentRepoShouldThrow };
        var auditRepo = new FakeAuditRepo();
        var connFactory = new FakeSqlConnectionFactory(onExecuteNonQuery);
        var resolution = new ConsentResolutionService();
        var evaluator = new WriteGrantEvaluator(consentRepo, allowDeleteGrants: allowDeleteGrants);
        var typedPolicies = GovernedTestSupport.TypedPolicies(options);
        IMandatoryRowFilterResolver mandatory = mandatoryStatusOpen
            ? new StatusOpenMandatoryFilterResolver()
            : NullMandatoryRowFilterResolver.Instance;

        var pdp = new AccessDecisionPoint(
            options,
            resolution,
            consentRepo,
            mandatoryFilters: mandatory,
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
            mandatoryFilters: mandatory,
            accessDecisionPoint: pdp);

        return new Harness(service, pdp, resolution, metadata, connFactory);
    }

    private sealed class Harness(
        GovernedSqlExecutionService service,
        AccessDecisionPoint pdp,
        ConsentResolutionService resolution,
        TableMetadata metadata,
        FakeSqlConnectionFactory connections)
    {
        public GovernedSqlExecutionService Service { get; } = service;
        public AccessDecisionPoint Pdp { get; } = pdp;
        public ConsentResolutionService Resolution { get; } = resolution;
        public TableMetadata Metadata { get; } = metadata;
        public FakeSqlConnectionFactory Connections { get; } = connections;
    }

    private sealed class StatusOpenMandatoryFilterResolver : IMandatoryRowFilterResolver
    {
        public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default)
        {
            var expr = new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("status", true)])),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__vf_status", SqlParameterType.String));
            var typed = PolicyPredicate.Create(
                expr,
                new Dictionary<string, PolicyValue> { ["__vf_status"] = new("OPEN", SqlParameterType.String) });
            var outcome = new MandatoryFilterOutcome(false, null, "(\"status\" = @__vf_status)", ["status_open"])
            {
                TypedPredicate = typed
            };
            return ValueTask.FromResult(outcome);
        }

        public ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    private sealed class FakeMetadataRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier t, CancellationToken ct = default) =>
            Task.FromResult(t.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier t, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
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
        public bool ShouldThrow { get; set; }
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Consent store failure");
            }

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
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeSqlConnectionFactory(Func<string, IReadOnlyDictionary<string, object?>, int>? onExecuteNonQuery) : ISqlConnectionFactory
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
            public override int ExecuteNonQuery()
            {
                var p = _params.ToDict();
                owner.ExecutedCommands.Add((CommandText, p));
                return owner.OnExecute(CommandText, p);
            }
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
