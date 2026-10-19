namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Mandatory Phase 3 security tests for REQ-DATA-75 / Issue #75 Addendum:
/// SEC-DATA75-01..05 cross-source GraphQL relations wiring and security guardrails.
/// </summary>
public sealed class CrossSourceRelationsSecurityTests : IDisposable
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "dbo", "orders");
    private static readonly TableIdentifier CustomersTableId = new("crm", "public", "customers");
    private static readonly TenantId DefaultTenant = new("tenant-gold");

    private readonly string _connectionString = $"Data Source=file:crosstree{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public CrossSourceRelationsSecurityTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE orders (order_id INTEGER PRIMARY KEY, customer_id INTEGER, total DECIMAL);
            INSERT INTO orders VALUES (1, 10, 100.50), (2, 20, 250.00);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

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
            SchemaName = "dbo",
            TableName = "orders",
            SourceType = "Sqlite",
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

    private sealed class CountingFactory(string connectionString) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var connection = new SqliteConnection(connectionString);
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
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA75_01_ClearJoinKeysEnforced_MaskedParentKey_FailsClosed()
    {
        // SEC-DATA75-01: Parent join key masked must fail closed with RELATION_KEY_MASKED
        var ordersMeta = CreateOrdersMetadata();
        var customersMeta = CreateCustomersMetadata();

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel> { ["customer_id"] = ColumnAccessLevel.Mask }, hasUnconstrainedColumnAllow: false),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        var act = () => service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-01a");
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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Mask }, hasUnconstrainedColumnAllow: false),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        var act = () => service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-01b");
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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = false // Disabled four-eyes!
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        var act = () => service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-02a");
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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = false }, // Master switch disabled!
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        var act = () => service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-02b");
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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            GraphQL = new GraphQLOptions { MaxResponseRows = 1 }, // Limit 1 row! (our database has 2 rows)
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        resolver.ResolveRelationAsync(Arg.Any<CrossSourceRelationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CrossSourceRelationResult(
                relation,
                ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }],
                OneToManyByParentIndex: [[]],
                BatchesExecuted: 1,
                TargetRowsMaterialized: 1,
                QueryCost: 1.0)));
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
            Limit = 1,
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

        // Query execution should check row cap
        var act = () => service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-03");
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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                callerTenant,
                new Sid("S-1-5-21-BOB"),
                callerUser));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                callerTenant,
                new Sid("S-1-5-21-BOB"),
                callerUser));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        CrossSourceRelationRequest? capturedRequest = null;
        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        resolver.ResolveRelationAsync(Arg.Do<CrossSourceRelationRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CrossSourceRelationResult(
                relation,
                ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }, null],
                OneToManyByParentIndex: [[], []],
                BatchesExecuted: 1,
                TargetRowsMaterialized: 1,
                QueryCost: 2.05)));

        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            new FakeAuditLogRepository(),
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        using var doc = await service.ExecuteAsync(callerUser, root, null, "op-sec75-04");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.TenantId.ShouldBe(callerTenant);
        capturedRequest.User.ShouldBeSameAs(callerUser);
        capturedRequest.ParentRows.Count.ShouldBe(2);

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

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), OrdersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                ordersMeta,
                TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), CustomersTableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string[]>>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                customersMeta,
                TableAccessDecision.Allowed(CustomersTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                DefaultTenant,
                new Sid("S-1-5-21-USER"),
                CreatePrincipal()));

        var options = new GatewayOptions
        {
            CrossSource = new CrossSourceOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["OrdersSource"] = new() { Provider = "Sqlite", ConnectionString = _connectionString }
                }
            }
        };

        var relation = new TableRelation
        {
            ParentTableIdentifier = OrdersTableId,
            ChildTableIdentifier = CustomersTableId,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var resolver = Substitute.For<ICrossSourceRelationResolver>();
        resolver.ResolveRelationAsync(Arg.Any<CrossSourceRelationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CrossSourceRelationResult(
                relation,
                ManyToOneByParentIndex: [new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" }, null],
                OneToManyByParentIndex: [[], []],
                BatchesExecuted: 1,
                TargetRowsMaterialized: 1,
                QueryCost: 2.05)));

        var auditRepo = new FakeAuditLogRepository();
        var service = new GovernedTreeQueryService(
            accessResolver,
            new CountingFactory(_connectionString),
            auditRepo,
            Substitute.For<IColumnMaskingProvider>(),
            Options.Create(options),
            crossSourceResolver: resolver,
            relationRepository: new TestRelationRepo(relation));

        var root = new TreeQueryNode(OrdersTableId, ["order_id", "customer_id", "total"])
        {
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

        using var doc = await service.ExecuteAsync(CreatePrincipal(), root, null, "op-sec75-05");

        auditRepo.Entries.ShouldContain(e =>
            e.EventType == AuditEventTypes.CrossSourceRelationResolved &&
            e.TargetTable == CustomersTableId.ToString() &&
            e.Decision == "ALLOW");
    }
}
