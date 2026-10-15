namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Changes;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class CrossSourceRelationsAndChangesSinceTests
{
    private static readonly TenantId DefaultTenant = new("tenant-a");

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-1001", string tenant = "tenant-a")
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

    private static TableMetadata CreateOrdersTable()
    {
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        return new TableMetadata
        {
            Identifier = tableId,
            PrimaryKeyColumns = ["order_id"],
            Table = new Table
            {
                SourceName = "SqlOrdersDb",
                SourceType = "PostgreSQL",
                DataSourceType = DataSourceType.Sql,
                SchemaName = "dbo",
                TableName = "orders",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "total_amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
    }

    private static TableMetadata CreateCustomersTable(DeletedKeyPolicy deletedKeyPolicy = DeletedKeyPolicy.KnownKeysOnly)
    {
        var tableId = new TableIdentifier("crm", "public", "customers");
        return new TableMetadata
        {
            Identifier = tableId,
            PrimaryKeyColumns = ["id"],
            DeletedKeyPolicy = deletedKeyPolicy,
            Table = new Table
            {
                SourceName = "CrmPostgresDb",
                SourceType = "PostgreSQL",
                DataSourceType = DataSourceType.Sql,
                SchemaName = "public",
                TableName = "customers",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***@***" }
            }
        };
    }

    // =========================================================================
    // ISSUE #75: Governed Cross-Source Relations
    // =========================================================================

    [Fact]
    public async Task ResolveRelationAsync_WhenAllowCrossDataSourceIsTrue_BatchesKeysAppliesSource2RlsAndMaskingAndAudits()
    {
        // Arrange
        var ordersMeta = CreateOrdersTable();
        var customersMeta = CreateCustomersTable();

        var metadataRepo = new FakeMetadataRepository(ordersMeta, customersMeta);
        var pdp = new FakePolicyDecisionPoint();

        // Source 1 (orders): all columns Clear
        pdp.SetDecision(ordersMeta.Identifier, TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["order_id"] = ColumnAccessLevel.Clear,
                ["customer_id"] = ColumnAccessLevel.Clear,
                ["total_amount"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true));

        // Source 2 (customers): RLS restricts to region = 'EU', email is Masked
        pdp.SetDecision(customersMeta.Identifier, TableAccessDecision.Allowed(
            customersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["region"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: "region = 'EU'"));

        var rowReader = new InMemoryCrossSourceRowReader();
        rowReader.SeedTable(customersMeta.Identifier,
        [
            new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice GmbH", ["email"] = "alice@example.de", ["region"] = "EU", ["tenant_id"] = "tenant-a" },
            new Dictionary<string, object?> { ["id"] = 20, ["name"] = "Bob Corp", ["email"] = "bob@example.us", ["region"] = "US", ["tenant_id"] = "tenant-a" },
            new Dictionary<string, object?> { ["id"] = 30, ["name"] = "Clara AG", ["email"] = "clara@example.de", ["region"] = "EU", ["tenant_id"] = "tenant-a" }
        ]);

        var auditRepo = new FakeAuditLogRepository();
        var resolver = new CrossSourceRelationResolver(
            metadataRepo,
            pdp,
            rowReader,
            new SimpleMaskingProvider(),
            auditRepo,
            options: Options.Create(new CrossSourceRelationOptions { MaxBatchSize = 2 }));

        var relation = new TableRelation
        {
            ParentTableIdentifier = ordersMeta.Identifier,
            ChildTableIdentifier = customersMeta.Identifier,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            Cardinality = RelationCardinality.ManyToOne,
            AllowCrossDataSource = true
        };

        var parentRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["order_id"] = 1, ["customer_id"] = 10, ["total_amount"] = 100m },
            new Dictionary<string, object?> { ["order_id"] = 2, ["customer_id"] = 20, ["total_amount"] = 200m }, // Customer 20 is in US -> hidden by Source 2 RLS!
            new Dictionary<string, object?> { ["order_id"] = 3, ["customer_id"] = 30, ["total_amount"] = 300m }
        };

        var user = CreatePrincipal();

        // Act
        var result = await resolver.ResolveRelationAsync(
            new CrossSourceRelationRequest(relation, parentRows, user, DefaultTenant));

        // Assert
        result.BatchesExecuted.Should().Be(2, "3 distinct keys with MaxBatchSize = 2 require 2 batches");
        result.TargetRowsMaterialized.Should().Be(2, "only the 2 EU customers pass Source 2 RLS");
        result.QueryCost.Should().BeGreaterThan(0);

        // Parent 0 (customer 10 - EU): resolved with masked email
        result.ManyToOneByParentIndex[0].Should().NotBeNull();
        result.ManyToOneByParentIndex[0]!["name"].Should().Be("Alice GmbH");
        result.ManyToOneByParentIndex[0]!["email"].Should().Be("***@***");

        // Parent 1 (customer 20 - US): hidden by Source 2 RLS -> indistinguishable from null / non-existent
        result.ManyToOneByParentIndex[1].Should().BeNull();
        result.OneToManyByParentIndex[1].Should().BeEmpty();

        // Parent 2 (customer 30 - EU): resolved with masked email
        result.ManyToOneByParentIndex[2].Should().NotBeNull();
        result.ManyToOneByParentIndex[2]!["name"].Should().Be("Clara AG");

        // Dedicated audit log entry recorded for Source 2
        auditRepo.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventTypes.WebSqlCrossSourceSourceRead &&
            e.TargetTable == customersMeta.Identifier.ToString() &&
            e.Decision == "ALLOW");
    }

    [Fact]
    public async Task ResolveRelationAsync_WhenAllowCrossDataSourceIsFalse_ThrowsCrossSourceRelationNotEnabled()
    {
        // Arrange
        var ordersMeta = CreateOrdersTable();
        var customersMeta = CreateCustomersTable();
        var resolver = new CrossSourceRelationResolver(
            new FakeMetadataRepository(ordersMeta, customersMeta),
            new FakePolicyDecisionPoint(),
            new InMemoryCrossSourceRowReader(),
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository());

        var relation = new TableRelation
        {
            ParentTableIdentifier = ordersMeta.Identifier,
            ChildTableIdentifier = customersMeta.Identifier,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = false
        };

        var act = () => resolver.ResolveRelationAsync(
            new CrossSourceRelationRequest(relation, [], CreatePrincipal(), DefaultTenant));

        // Assert
        var ex = await act.Should().ThrowAsync<CrossSourceRelationException>();
        ex.Which.ErrorCode.Should().Be(CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled);
    }

    [Theory]
    [InlineData(ColumnAccessLevel.Mask, ColumnAccessLevel.Clear)]
    [InlineData(ColumnAccessLevel.Deny, ColumnAccessLevel.Clear)]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Mask)]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Deny)]
    public async Task ResolveRelationAsync_WhenJoinKeyNotClearOnEitherSide_ThrowsCrossSourceRelationKeyNotClear(
        ColumnAccessLevel parentJoinKeyAccess,
        ColumnAccessLevel childJoinKeyAccess)
    {
        // Arrange
        var ordersMeta = CreateOrdersTable();
        var customersMeta = CreateCustomersTable();
        var pdp = new FakePolicyDecisionPoint();

        pdp.SetDecision(ordersMeta.Identifier, TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["order_id"] = ColumnAccessLevel.Clear,
                ["customer_id"] = parentJoinKeyAccess
            }));

        pdp.SetDecision(customersMeta.Identifier, TableAccessDecision.Allowed(
            customersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = childJoinKeyAccess,
                ["name"] = ColumnAccessLevel.Clear
            }));

        var resolver = new CrossSourceRelationResolver(
            new FakeMetadataRepository(ordersMeta, customersMeta),
            pdp,
            new InMemoryCrossSourceRowReader(),
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository());

        var relation = new TableRelation
        {
            ParentTableIdentifier = ordersMeta.Identifier,
            ChildTableIdentifier = customersMeta.Identifier,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        var act = () => resolver.ResolveRelationAsync(
            new CrossSourceRelationRequest(relation, [], CreatePrincipal(), DefaultTenant));

        // Assert
        var ex = await act.Should().ThrowAsync<CrossSourceRelationException>();
        ex.Which.ErrorCode.Should().Be(CrossSourceRelationErrorCodes.CrossSourceRelationKeyNotClear);
    }

    [Fact]
    public async Task CrossSourceFilterAndSort_EnforcesMaxFilterKeySetAndRejectsCrossSourceSort()
    {
        // Arrange
        var ordersMeta = CreateOrdersTable();
        var customersMeta = CreateCustomersTable();
        var pdp = new FakePolicyDecisionPoint();

        pdp.SetDecision(ordersMeta.Identifier, TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["order_id"] = ColumnAccessLevel.Clear,
                ["customer_id"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true));

        pdp.SetDecision(customersMeta.Identifier, TableAccessDecision.Allowed(
            customersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["region"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true));

        var rowReader = new InMemoryCrossSourceRowReader();
        rowReader.SeedTable(customersMeta.Identifier,
        [
            new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU", ["tenant_id"] = "tenant-a" },
            new Dictionary<string, object?> { ["id"] = 2, ["region"] = "EU", ["tenant_id"] = "tenant-a" },
            new Dictionary<string, object?> { ["id"] = 3, ["region"] = "EU", ["tenant_id"] = "tenant-a" }
        ]);

        var resolver = new CrossSourceRelationResolver(
            new FakeMetadataRepository(ordersMeta, customersMeta),
            pdp,
            rowReader,
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository(),
            options: Options.Create(new CrossSourceRelationOptions { MaxCrossSourceFilterKeys = 2 }));

        var relation = new TableRelation
        {
            ParentTableIdentifier = ordersMeta.Identifier,
            ChildTableIdentifier = customersMeta.Identifier,
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"],
            AllowCrossDataSource = true
        };

        // 1. Filter matching 3 keys when MaxCrossSourceFilterKeys = 2 fails closed with CROSS_SOURCE_FILTER_KEY_SET_EXCEEDED
        var exceedAct = () => resolver.ResolveCrossSourceFilterKeysAsync(
            new CrossSourceFilterPushdownRequest(relation, "region = 'EU'", ["region"], CreatePrincipal(), DefaultTenant));

        var exceedEx = await exceedAct.Should().ThrowAsync<CrossSourceRelationException>();
        exceedEx.Which.ErrorCode.Should().Be(CrossSourceRelationErrorCodes.CrossSourceFilterKeySetExceeded);

        // 2. Sorting parent by cross-source relation fails with CROSS_SOURCE_SORT_NOT_SUPPORTED
        var sortAct = () => resolver.ResolveRelationAsync(
            new CrossSourceRelationRequest(
                relation,
                [],
                CreatePrincipal(),
                DefaultTenant,
                SortByTargetColumns: ["name"]));

        var sortEx = await sortAct.Should().ThrowAsync<CrossSourceRelationException>();
        sortEx.Which.ErrorCode.Should().Be(CrossSourceRelationErrorCodes.CrossSourceSortNotSupported);
    }

    // =========================================================================
    // ISSUE #77: Governed Change Detection (ETag) & Stateless changesSince Cursor
    // =========================================================================

    [Fact]
    public async Task CheckTableModifiedAsync_WithMatchingETag_ReturnsNotModifiedWithZeroRowReadsAndZeroCost()
    {
        // Arrange
        var customersMeta = CreateCustomersTable();
        var ordersMeta = CreateOrdersTable(); // We won't seed a version for ordersMeta to test unsupported source
        var metadataRepo = new FakeMetadataRepository(customersMeta, ordersMeta);
        var pdp = new FakePolicyDecisionPoint();
        pdp.SetDecision(customersMeta.Identifier, TableAccessDecision.Allowed(
            customersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true));
        pdp.SetDecision(ordersMeta.Identifier, TableAccessDecision.Allowed(
            ordersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true));

        var versionStore = new InMemoryChangeVersionStore();
        versionStore.SetTableVersion(customersMeta.Identifier, currentVersion: 42L, minValidVersion: 1L);

        var service = new GovernedChangeDetectionService(
            metadataRepo,
            pdp,
            versionStore,
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository());

        var user = CreatePrincipal();

        // 1. Initial check without If-None-Match returns current ETag
        var initial = await service.CheckTableModifiedAsync(customersMeta.Identifier, user, DefaultTenant);
        initial.SupportsChangeTracking.Should().BeTrue();
        initial.NotModified.Should().BeFalse();
        initial.ChangeToken.Should().NotBeNullOrWhiteSpace();
        initial.CurrentVersion.Should().Be(42L);
        versionStore.RowReadInvocationCount.Should().Be(0);

        // 2. Subsequent check with matching If-None-Match returns NotModified = true, QueryCost = 0, and 0 row reads
        var second = await service.CheckTableModifiedAsync(
            customersMeta.Identifier,
            user,
            DefaultTenant,
            ifNoneMatchToken: initial.ChangeToken);

        second.NotModified.Should().BeTrue();
        second.QueryCost.Should().Be(0.0);
        second.ChangeToken.Should().Be(initial.ChangeToken);
        versionStore.RowReadInvocationCount.Should().Be(0, "304 Not Modified must not read any rows from the table");

        // 3. After a table version bump, old token no longer matches
        versionStore.SetTableVersion(customersMeta.Identifier, currentVersion: 43L, minValidVersion: 1L);
        var afterWrite = await service.CheckTableModifiedAsync(
            customersMeta.Identifier,
            user,
            DefaultTenant,
            ifNoneMatchToken: initial.ChangeToken);

        afterWrite.NotModified.Should().BeFalse();
        afterWrite.CurrentVersion.Should().Be(43L);
        afterWrite.ChangeToken.Should().NotBe(initial.ChangeToken);

        // 4. Table on a source without change tracking returns SupportsChangeTracking = false and null token
        var unsupported = await service.CheckTableModifiedAsync(ordersMeta.Identifier, user, DefaultTenant);
        unsupported.SupportsChangeTracking.Should().BeFalse();
        unsupported.ChangeToken.Should().BeNull();
    }

    [Fact]
    public async Task GetChangesSinceAsync_ComputesNetUpsertsAndRemovalsIncludingRlsExitAndRespectsDeletedKeyPolicy()
    {
        // Arrange
        var customersKnownOnly = CreateCustomersTable(DeletedKeyPolicy.KnownKeysOnly);
        var metadataRepo = new FakeMetadataRepository(customersKnownOnly);
        var pdp = new FakePolicyDecisionPoint();

        // Caller only sees region = 'EU'; email is masked
        pdp.SetDecision(customersKnownOnly.Identifier, TableAccessDecision.Allowed(
            customersKnownOnly.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["region"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: "region = 'EU'"));

        var versionStore = new InMemoryChangeVersionStore();
        versionStore.SetTableVersion(customersKnownOnly.Identifier, currentVersion: 10L, minValidVersion: 5L);

        var service = new GovernedChangeDetectionService(
            metadataRepo,
            pdp,
            versionStore,
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository());

        var user = CreatePrincipal("user-eu-1");
        var cursorV10 = service.IssueCursor(customersKnownOnly.Identifier, user, DefaultTenant, syncVersion: 10L);

        // Changes between v10 and v15:
        // - id=1: Inserted in EU (matches RLS) -> should be in Upserted (with masked email)
        // - id=2: Updated from region='EU' to region='US' (RLS-exit transition!) -> should be in Removed!
        // - id=3: Physically deleted, AND caller passes id=3 in KnownKeys -> should be in Removed!
        // - id=99: Physically deleted foreign row (not in KnownKeys, no pre-image) -> must NOT be leaked under KnownKeysOnly!
        versionStore.RecordChange(
            customersKnownOnly.Identifier,
            new SourceChangeRecord(
                new Dictionary<string, object?> { ["id"] = 1 },
                CdcOperation.Insert,
                ChangeVersion: 11L));

        versionStore.RecordChange(
            customersKnownOnly.Identifier,
            new SourceChangeRecord(
                new Dictionary<string, object?> { ["id"] = 2 },
                CdcOperation.Update,
                ChangeVersion: 12L,
                PreImage: new Dictionary<string, object?> { ["id"] = 2, ["region"] = "EU", ["tenant_id"] = "tenant-a" }));

        versionStore.RecordChange(
            customersKnownOnly.Identifier,
            new SourceChangeRecord(
                new Dictionary<string, object?> { ["id"] = 3 },
                CdcOperation.Delete,
                ChangeVersion: 13L));

        versionStore.RecordChange(
            customersKnownOnly.Identifier,
            new SourceChangeRecord(
                new Dictionary<string, object?> { ["id"] = 99 },
                CdcOperation.Delete,
                ChangeVersion: 14L),
            newCurrentVersion: 15L);

        versionStore.SetCurrentRows(customersKnownOnly.Identifier,
        [
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "New EU Customer", ["email"] = "new@eu.example", ["region"] = "EU", ["tenant_id"] = "tenant-a" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Moved To US", ["email"] = "moved@us.example", ["region"] = "US", ["tenant_id"] = "tenant-a" }
        ]);

        var knownKeys = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 2 },
            new Dictionary<string, object?> { ["id"] = 3 }
        };

        // Act 1: KnownKeysOnly policy
        var deltaKnownOnly = await service.GetChangesSinceAsync(
            new ChangesSinceRequest(
                customersKnownOnly.Identifier,
                cursorV10,
                user,
                DefaultTenant,
                KnownKeys: knownKeys));

        // Assert 1
        deltaKnownOnly.SyncVersion.Should().Be(15L);
        deltaKnownOnly.Upserted.Should().ContainSingle();
        deltaKnownOnly.Upserted[0]["id"].Should().Be(1);
        deltaKnownOnly.Upserted[0]["email"].Should().Be("***@***", "masked columns must remain masked in Upserted");

        var removedIdsKnownOnly = deltaKnownOnly.Removed.Select(r => Convert.ToInt32(r["id"])).ToList();
        removedIdsKnownOnly.Should().BeEquivalentTo([2, 3], "id=2 (RLS-exit) and id=3 (in KnownKeys) are returned, while foreign deleted key id=99 is not leaked");

        // Act 2: Opt-in AllDeletedKeys policy on the table
        var customersAllDeleted = CreateCustomersTable(DeletedKeyPolicy.AllDeletedKeys);
        metadataRepo.Upsert(customersAllDeleted);

        var deltaAllDeleted = await service.GetChangesSinceAsync(
            new ChangesSinceRequest(
                customersAllDeleted.Identifier,
                cursorV10,
                user,
                DefaultTenant,
                KnownKeys: null));

        var removedIdsAll = deltaAllDeleted.Removed.Select(r => Convert.ToInt32(r["id"])).ToList();
        removedIdsAll.Should().BeEquivalentTo([2, 3, 99], "under DeletedKeyPolicy.AllDeletedKeys, all deleted/RLS-exited keys are disclosed");
    }

    [Fact]
    public async Task GetChangesSinceAsync_EnforcesCursorCallerBindingAndMinValidVersionExpiration()
    {
        // Arrange
        var customersMeta = CreateCustomersTable();
        var metadataRepo = new FakeMetadataRepository(customersMeta);
        var pdp = new FakePolicyDecisionPoint();
        pdp.SetDecision(customersMeta.Identifier, TableAccessDecision.Allowed(
            customersMeta.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true));

        var versionStore = new InMemoryChangeVersionStore();
        versionStore.SetTableVersion(customersMeta.Identifier, currentVersion: 50L, minValidVersion: 20L);

        var service = new GovernedChangeDetectionService(
            metadataRepo,
            pdp,
            versionStore,
            new SimpleMaskingProvider(),
            new FakeAuditLogRepository());

        var personA = CreatePrincipal("S-1-5-21-PERSON-A");
        var personB = CreatePrincipal("S-1-5-21-PERSON-B");

        // 1. Cursor issued to Person A used by Person B -> CURSOR_CALLER_MISMATCH
        var cursorA = service.IssueCursor(customersMeta.Identifier, personA, DefaultTenant, syncVersion: 30L);
        var mismatchAct = () => service.GetChangesSinceAsync(
            new ChangesSinceRequest(customersMeta.Identifier, cursorA, personB, DefaultTenant));

        var mismatchEx = await mismatchAct.Should().ThrowAsync<GatewaySecurityException>();
        mismatchEx.Which.ErrorCode.Should().Be(ChangeDetectionErrorCodes.CursorCallerMismatch);

        // 2. Cursor with SyncVersion = 10 (< MinValidVersion = 20) -> CURSOR_EXPIRED
        var expiredCursor = service.IssueCursor(customersMeta.Identifier, personA, DefaultTenant, syncVersion: 10L);
        var expiredAct = () => service.GetChangesSinceAsync(
            new ChangesSinceRequest(customersMeta.Identifier, expiredCursor, personA, DefaultTenant));

        var expiredEx = await expiredAct.Should().ThrowAsync<GatewaySecurityException>();
        expiredEx.Which.ErrorCode.Should().Be(ChangeDetectionErrorCodes.CursorExpired);
    }

    // =========================================================================
    // Test Doubles
    // =========================================================================

    private sealed class FakeMetadataRepository : ITableMetadataRepository
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _tables = new();

        public FakeMetadataRepository(params TableMetadata[] tables)
        {
            foreach (var t in tables)
            {
                _tables[t.Identifier] = t;
            }
        }

        public void Upsert(TableMetadata metadata) => _tables[metadata.Identifier] = metadata;

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.TryGetValue(table, out var m) ? m : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }
    }

    private sealed class FakePolicyDecisionPoint : IUnifiedPolicyDecisionPoint
    {
        private readonly Dictionary<TableIdentifier, TableAccessDecision> _decisions = new();

        public void SetDecision(TableIdentifier table, TableAccessDecision decision) =>
            _decisions[table] = decision;

        public Task<TableAccessDecision> EvaluateAccessAsync(
            TableIdentifier table,
            TableMetadata metadata,
            SecurityPrincipalContext securityContext,
            IReadOnlyList<string>? requestedColumns,
            CancellationToken ct)
        {
            if (_decisions.TryGetValue(table, out var decision))
            {
                return Task.FromResult(decision);
            }

            return Task.FromResult(TableAccessDecision.Denied(table, "No decision configured."));
        }
    }

    private sealed class SimpleMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? value, MaskingRule rule) =>
            value is null ? null : (rule.Replacement ?? "[REDACTED]");
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
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
