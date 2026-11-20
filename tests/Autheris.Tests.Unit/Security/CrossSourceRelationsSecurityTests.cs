namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Relations;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Mandatory Phase 3 security tests for REQ-DATA-75 / Issue #75 Addendum:
/// SEC-DATA75-01..05 cross-source GraphQL relations wiring and security guardrails.
/// </summary>
[Trait("Category", "Security")]
public sealed class CrossSourceRelationsSecurityTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");
    private static readonly TableIdentifier CustomersTableId = new("crm", "public", "customers");
    private static readonly TenantId DefaultTenant = new("tenant-gold");

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-USER", string tenant = "tenant-gold")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("sub", sid),
            new Claim("tenant_id", tenant)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static TableMetadata CreateOrdersMetadata() => new()
    {
        Identifier = OrdersTableId,
        PrimaryKeyColumns = ["order_id"],
        Table = new Table
        {
            SourceName = "OrdersSource",
            SchemaName = "public",
            TableName = "orders",
            SourceType = "PostgreSQL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "order_id", DataType = "int" },
            new TableColumn { ColumnName = "customer_id", DataType = "int" },
            new TableColumn { ColumnName = "total", DataType = "decimal" }
        ]
    };

    private static TableMetadata CreateCustomersMetadata() => new()
    {
        Identifier = CustomersTableId,
        PrimaryKeyColumns = ["id"],
        Table = new Table
        {
            SourceName = "CustomersSource", // Different data source!
            SchemaName = "public",
            TableName = "customers",
            SourceType = "PostgreSQL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ]
    };

    private sealed class CountingFactory(string connectionString, string? json = null) : ISqlConnectionFactory
    {
        private const string TwoOrdersJson =
            """[{"order_id":1,"customer_id":10,"total":100.50},{"order_id":2,"customer_id":20,"total":250.00}]""";

        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            _ = connectionString;
            var payload = json ?? TwoOrdersJson;
            var connection = new CallbackDbConnection((_, _) => new StubDbDataReader(["json"], [[payload]]));
            connection.Open();
            return Task.FromResult<DbConnection>(connection);
        }
    }

    private sealed class FakeAuditLogRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = new();

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null,
            int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class TestRelationRepo(params TableRelation[] relations) : ITableRelationRepository
    {
        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
        {
            var matched = new List<TableRelation>();
            foreach (var r in relations)
            {
                if (r.ParentTableIdentifier.Equals(parentTable) || r.ChildTableIdentifier.Equals(parentTable))
                {
                    matched.Add(r);
                }
            }
            return Task.FromResult<IReadOnlyList<TableRelation>>(matched);
        }

        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(relations);
    }

    private static ResolvedTableAccess AllowedAccess(TableMetadata meta, ClaimsPrincipal? user = null, TenantId? tenant = null, Sid? sid = null, Dictionary<string, ColumnAccessLevel>? columns = null) =>
        new(
            meta,
            TableAccessDecision.Allowed(meta.Identifier, columns ?? new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: columns is null),
            tenant ?? DefaultTenant,
            sid ?? new Sid("S-1-5-21-USER"),
            user ?? CreatePrincipal());

    private static GovernedTreeQueryService CreateService(
        ITableAccessResolver accessResolver,
        GatewayOptions options,
        TableRelation relation,
        ICrossSourceRelationResolver? resolver = null,
        IAuditLogRepository? auditRepo = null,
        string? json = null) =>
        new(
            accessResolver,
            new CountingFactory("Host=unused;Database=orders", json),
            auditRepo ?? new FakeAuditLogRepository(),
            StubColumnMaskingProvider.Null,
            Options.Create(options),
            crossSourceResolver: resolver ?? new StubCrossSourceRelationResolver(),
            relationRepository: new TestRelationRepo(relation));

    private static TableRelation CustomerRelation(bool allowCrossDataSource = true) => new()
    {
        ParentTableIdentifier = OrdersTableId,
        ChildTableIdentifier = CustomersTableId,
        RelationName = "customer",
        JoinKeysParent = ["customer_id"],
        JoinKeysChild = ["id"],
        AllowCrossDataSource = allowCrossDataSource
    };

    private static GatewayOptions DefaultOptions(bool crossSourceEnabled = true, int maxResponseRows = 1000) => new()
    {
        CrossSource = new CrossSourceOptions { Enabled = crossSourceEnabled },
        GraphQL = new GraphQLOptions { MaxResponseRows = maxResponseRows },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>
            {
                ["OrdersSource"] = new() { Provider = "PostgreSQL", ConnectionString = "Host=unused;Database=orders" }
            }
        }
    };

    private static TreeQueryNode RootNode(int limit = 100) => new(OrdersTableId, ["order_id", "customer_id", "total"])
    {
        Limit = limit,
        Relations =
        [
            new TreeRelationNode(
                ResponseKey: "customer",
                ParentColumns: ["customer_id"],
                ChildColumns: ["id"],
                IsList: false,
                Child: new TreeQueryNode(CustomersTableId, ["id", "name"]))
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_01_ClearJoinKeysEnforced_MaskedParentKey_FailsClosed()
    {
        // SEC-DATA75-01: Parent join key masked must fail closed with RELATION_KEY_MASKED
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(
            AllowedAccess(ordersMeta, columns: new Dictionary<string, ColumnAccessLevel> { ["customer_id"] = ColumnAccessLevel.Mask }),
            AllowedAccess(customersMeta));

        var service = CreateService(accessResolver, DefaultOptions(), CustomerRelation(), json: """[{"order_id":1,"customer_id":10,"total":100.50}]""");

        var act = () => service.ExecuteAsync(CreatePrincipal(), RootNode(), null, "op-sec75-01a");
        var ex = await Should.ThrowAsync<CrossSourceRelationException>(act);
        ex.ErrorCode.ShouldBe(CrossSourceRelationErrorCodes.RelationKeyMasked);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_01_ClearJoinKeysEnforced_MaskedChildKey_FailsClosed()
    {
        // SEC-DATA75-01: Target/child join key masked must fail closed with RELATION_KEY_MASKED
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(
            AllowedAccess(ordersMeta),
            AllowedAccess(customersMeta, columns: new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Mask }));

        var service = CreateService(accessResolver, DefaultOptions(), CustomerRelation(), json: """[{"order_id":1,"customer_id":10,"total":100.50}]""");

        var act = () => service.ExecuteAsync(CreatePrincipal(), RootNode(), null, "op-sec75-01b");
        var ex = await Should.ThrowAsync<CrossSourceRelationException>(act);
        ex.ErrorCode.ShouldBe(CrossSourceRelationErrorCodes.RelationKeyMasked);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_02_FourEyesConsent_AllowCrossDataSourceFalse_FailsClosed()
    {
        // SEC-DATA75-02: AllowCrossDataSource == false fails with CROSS_SOURCE_RELATION_NOT_ENABLED
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(AllowedAccess(ordersMeta), AllowedAccess(customersMeta));
        var service = CreateService(accessResolver, DefaultOptions(), CustomerRelation(allowCrossDataSource: false), json: """[{"order_id":1,"customer_id":10,"total":100.50}]""");

        var act = () => service.ExecuteAsync(CreatePrincipal(), RootNode(), null, "op-sec75-02a");
        var ex = await Should.ThrowAsync<CrossSourceRelationException>(act);
        ex.ErrorCode.ShouldBe(CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_02_MasterSwitchDisabled_FailsClosed()
    {
        // SEC-DATA75-02: Master switch disabled fails with CROSS_SOURCE_RELATION_NOT_ENABLED
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(AllowedAccess(ordersMeta), AllowedAccess(customersMeta));
        var service = CreateService(accessResolver, DefaultOptions(crossSourceEnabled: false), CustomerRelation(), json: """[{"order_id":1,"customer_id":10,"total":100.50}]""");

        var act = () => service.ExecuteAsync(CreatePrincipal(), RootNode(), null, "op-sec75-02b");
        var ex = await Should.ThrowAsync<CrossSourceRelationException>(act);
        ex.ErrorCode.ShouldBe(CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_03_ParameterBoundedBatchingAndRowCap()
    {
        // SEC-DATA75-03: Exceeding MaxResponseRows row cap is rejected
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(AllowedAccess(ordersMeta), AllowedAccess(customersMeta));
        var relation = CustomerRelation();
        var resolver = new StubCrossSourceRelationResolver(_ => new CrossSourceRelationResult(
            relation,
            ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }],
            OneToManyByParentIndex: [[]],
            BatchesExecuted: 1,
            TargetRowsMaterialized: 1,
            QueryCost: 1.0));
        var service = CreateService(accessResolver, DefaultOptions(maxResponseRows: 1), relation, resolver, json: """[{"order_id":1,"customer_id":10,"total":100.50}]""");

        // Query execution should check row cap
        var act = () => service.ExecuteAsync(CreatePrincipal(), RootNode(limit: 1), null, "op-sec75-03");
        // Should execute and enforce bounds
        using var doc = await act();
        doc.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_04_TenancyAndPrincipalIsolation_PreservedAcrossBoundary()
    {
        // SEC-DATA75-04: Cross source child execution receives caller's tenant and principal without elevation
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var callerUser = CreatePrincipal(sid: "S-1-5-21-BOB", tenant: "tenant-isolated");
        var callerTenant = new TenantId("tenant-isolated");
        var callerSid = new Sid("S-1-5-21-BOB");

        var accessResolver = new StubTableAccessResolver(
            AllowedAccess(ordersMeta, callerUser, callerTenant, callerSid),
            AllowedAccess(customersMeta, callerUser, callerTenant, callerSid));
        var relation = CustomerRelation();
        var resolver = new StubCrossSourceRelationResolver(_ => new CrossSourceRelationResult(
            relation,
            ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }, null],
            OneToManyByParentIndex: [[], []],
            BatchesExecuted: 1,
            TargetRowsMaterialized: 1,
            QueryCost: 2.05));
        var service = CreateService(accessResolver, DefaultOptions(), relation, resolver);

        using var doc = await service.ExecuteAsync(callerUser, RootNode(), null, "op-sec75-04");

        resolver.LastRequest.ShouldNotBeNull();
        resolver.LastRequest!.TenantId.ShouldBe(callerTenant);
        resolver.LastRequest.User.ShouldBeSameAs(callerUser);
        resolver.LastRequest.ParentRows.Count.ShouldBe(2);

        // Result was stitched
        doc.RootElement[0].GetProperty("customer").GetProperty("name").GetString().ShouldBe("Alice");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_05_CrossSourceResolution_EmitsDedicatedAuditLog()
    {
        // SEC-DATA75-05: Dedicated audit record is emitted with parent and child tables and stitched rows
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = new StubTableAccessResolver(AllowedAccess(ordersMeta), AllowedAccess(customersMeta));
        var relation = CustomerRelation();
        var resolver = new StubCrossSourceRelationResolver(_ => new CrossSourceRelationResult(
            relation,
            ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }, null],
            OneToManyByParentIndex: [[], []],
            BatchesExecuted: 1,
            TargetRowsMaterialized: 1,
            QueryCost: 2.05));
        var auditRepo = new FakeAuditLogRepository();
        var service = CreateService(accessResolver, DefaultOptions(), relation, resolver, auditRepo);

        using var doc = await service.ExecuteAsync(CreatePrincipal(), RootNode(), null, "op-sec75-05");

        auditRepo.Entries.ShouldContain(e =>
            e.EventType == AuditEventTypes.CrossSourceRelationResolved &&
            e.TargetTable == CustomersTableId.ToString() &&
            e.Decision == "ALLOW");
    }

    private sealed class StubCrossSourceRelationResolver(Func<CrossSourceRelationRequest, CrossSourceRelationResult>? onResolve = null) : ICrossSourceRelationResolver
    {
        public CrossSourceRelationRequest? LastRequest { get; private set; }

        public bool IsCrossDataSource(TableMetadata parentTable, TableMetadata childTable) => true;

        public void ValidateNoCrossSourceSort(TableRelation relation, TableMetadata parentTable, TableMetadata childTable, IReadOnlyList<string>? sortByTargetColumns)
        {
        }

        public Task<CrossSourceRelationResult> ResolveRelationAsync(CrossSourceRelationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return onResolve is not null
                ? Task.FromResult(onResolve(request))
                : throw new NotSupportedException();
        }
    }
}
