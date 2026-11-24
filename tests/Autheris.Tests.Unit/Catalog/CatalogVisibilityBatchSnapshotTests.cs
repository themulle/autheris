namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogVisibilityBatchSnapshotTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid UserSid = new("S-1-USER");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    private static readonly TableIdentifier HiddenTable = new("hr", "dbo", "salaries");
    private static readonly TableIdentifier InactiveTable = new("sales", "dbo", "archived_orders");
    private static readonly TableIdentifier ForeignVirtual = new("semantic", SemanticVirtualTables.TenantSchema(TenantB), "metrics");

    [Fact]
    public async Task FindVisibleManyAsync_WithSnapshotProvider_ZeroMetadataRepoReads_SingleConsentRead()
    {
        var tables = new[] { MakeTable(Orders), MakeTable(Customers) };
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshotProvider = new StubSnapshotProvider(snapshot);
        var metadata = new CountingMetadataRepo();
        var consents = new CountingConsentRepo([
            Allow(Orders, "id", "amount"),
            Allow(Customers, "id", "name")
        ]);
        var service = CreateService(metadata, consents, snapshotProvider);
        var caller = new CatalogCaller(CallerPrincipal(), TenantA, CatalogChannel.Mcp);

        var single = await service.FindVisibleAsync(caller, Orders);
        var filtered = await service.FilterVisibleAsync(caller, [Orders, Customers, HiddenTable]);
        var batch = await service.FindVisibleManyAsync(caller, [Orders, Customers, HiddenTable]);

        single.ShouldNotBeNull();
        filtered.SetEquals([Orders, Customers]).ShouldBeTrue();
        batch.IsAuthoritative.ShouldBeTrue();
        batch.Rows.Keys.ToHashSet().SetEquals([Orders, Customers]).ShouldBeTrue();
        metadata.SingleReadCount.ShouldBe(0);
        consents.AllActiveCalls.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FindVisibleManyAsync_SnapshotThrowsOrNotReady_FailsClosedEmpty()
    {
        var metadata = new CountingMetadataRepo();
        var consents = new CountingConsentRepo([Allow(Orders, "id")]);
        var caller = new CatalogCaller(CallerPrincipal(), TenantA, CatalogChannel.Mcp);

        var unready = CreateService(metadata, consents, new StubSnapshotProvider(null) { ThrowNotReady = true });
        (await unready.FindVisibleAsync(caller, Orders)).ShouldBeNull();
        (await unready.FilterVisibleAsync(caller, [Orders])).ShouldBeEmpty();
        var unreadyBatch = await unready.FindVisibleManyAsync(caller, [Orders]);
        unreadyBatch.IsAuthoritative.ShouldBeTrue();
        unreadyBatch.Rows.ShouldBeEmpty();

        var broken = CreateService(metadata, consents, new StubSnapshotProvider(null) { ExceptionToThrow = new InvalidOperationException("snapshot down") });
        (await broken.FindVisibleAsync(caller, Orders)).ShouldBeNull();
        (await broken.FilterVisibleAsync(caller, [Orders])).ShouldBeEmpty();
        var brokenBatch = await broken.FindVisibleManyAsync(caller, [Orders]);
        brokenBatch.IsAuthoritative.ShouldBeTrue();
        brokenBatch.Rows.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FindVisibleManyAsync_StripsDeniedColumnsAndDropsInactiveUnconsentedAndForeignVirtualTables()
    {
        var tables = new[]
        {
            MakeTable(Orders),
            MakeTable(InactiveTable, isActive: false),
            MakeTable(HiddenTable),
            MakeTable(ForeignVirtual)
        };
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshotProvider = new StubSnapshotProvider(snapshot);
        var metadata = new CountingMetadataRepo();
        var consents = new CountingConsentRepo([
            new Consent
            {
                Id = Guid.NewGuid(),
                GranteeSid = UserSid,
                TenantId = TenantA,
                TableIdentifier = Orders,
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "secret", AccessLevel = ColumnAccessLevel.Deny }
                ]
            },
            Allow(InactiveTable, "id"),
            Allow(ForeignVirtual, "id")
        ]);

        var service = CreateService(metadata, consents, snapshotProvider);
        var caller = new CatalogCaller(CallerPrincipal(), TenantA, CatalogChannel.Mcp);

        var singleOrders = await service.FindVisibleAsync(caller, Orders);
        var singleInactive = await service.FindVisibleAsync(caller, InactiveTable);
        var singleUnconsented = await service.FindVisibleAsync(caller, HiddenTable);
        var singleForeign = await service.FindVisibleAsync(caller, ForeignVirtual);
        var batch = await service.FindVisibleManyAsync(caller, [Orders, InactiveTable, HiddenTable, ForeignVirtual]);

        singleOrders.ShouldNotBeNull();
        singleOrders.Value.Table.Columns.Select(c => c.ColumnName).ShouldBe(["id"]);
        singleInactive.ShouldBeNull();
        singleUnconsented.ShouldBeNull();
        singleForeign.ShouldBeNull();
        batch.Rows.Keys.ShouldHaveSingleItem().ShouldBe(Orders);
        batch.Rows[Orders].Table.Columns.Select(c => c.ColumnName).ShouldBe(["id"]);
        batch.Rows.ContainsKey(InactiveTable).ShouldBeFalse();
        batch.Rows.ContainsKey(HiddenTable).ShouldBeFalse();
        batch.Rows.ContainsKey(ForeignVirtual).ShouldBeFalse();
    }

    private static CatalogVisibilityService CreateService(
        ITableMetadataRepository metadata,
        IConsentRepository consents,
        ICatalogSnapshotProvider snapshotProvider) =>
        new(
            metadata,
            consents,
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            snapshotProvider: snapshotProvider);

    private static ClaimsPrincipal CallerPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, UserSid.Value), new Claim("tenant_id", TenantA.Value)],
            "MCP"));

    private static TableMetadata MakeTable(TableIdentifier id, bool isActive = true) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = isActive },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "secret", DataType = "varchar" }
        ]
    };

    private static Consent Allow(TableIdentifier table, params string[] columns) => new()
    {
        Id = Guid.NewGuid(),
        GranteeSid = UserSid,
        TenantId = TenantA,
        TableIdentifier = table,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = columns.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
    };

    private sealed class StubSnapshotProvider(CatalogSnapshot? snapshot) : ICatalogSnapshotProvider
    {
        public bool ThrowNotReady { get; init; }
        public Exception? ExceptionToThrow { get; init; }
        public bool IsInitialized => snapshot is not null && !ThrowNotReady;
        public CatalogSnapshot CurrentSnapshot => snapshot ?? CatalogSnapshot.Empty();

        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
            {
                return ValueTask.FromException<CatalogSnapshot>(ExceptionToThrow);
            }

            if (ThrowNotReady || snapshot is null)
            {
                return ValueTask.FromException<CatalogSnapshot>(new CatalogSnapshotNotReadyException());
            }

            return ValueTask.FromResult(snapshot);
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingMetadataRepo : ITableMetadataRepository
    {
        private int _singleReads;
        public int SingleReadCount => Volatile.Read(ref _singleReads);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _singleReads);
            return Task.FromResult<TableMetadata?>(null);
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        private int _allActiveCalls;
        public int AllActiveCalls => Volatile.Read(ref _allActiveCalls);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _allActiveCalls);
            return Task.FromResult(consents);
        }

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
