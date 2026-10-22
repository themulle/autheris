namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Changes;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>VF-11 / VF-13: changesSince discloses removals only with proof of visibility, is bounded, and is not advertised without a change log.</summary>
[Trait("Category", "Security")]
public sealed class ChangesSinceDisclosureTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Table = new("crm", "public", "customers");

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1")], "test"));

    private static TableMetadata Customers(bool tenanted = true, DeletedKeyPolicy policy = DeletedKeyPolicy.KnownKeysOnly) => new()
    {
        Identifier = Table,
        PrimaryKeyColumns = ["id"],
        DeletedKeyPolicy = policy,
        Table = new Table { SourceName = "crm", SourceType = "PostgreSQL", DataSourceType = DataSourceType.Sql, SchemaName = "public", TableName = "customers", IsActive = true },
        Columns = tenanted
            ? [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "varchar" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]
            : [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "varchar" }]
    };

    private static (GovernedChangeDetectionService Service, IChangeVersionProvider Provider, string Cursor) Setup(
        IChangeVersionProvider provider, TableMetadata metadata, int maxChanges = 1000)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Table, Arg.Any<CancellationToken>()).Returns(metadata);
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Table, Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(
                Table,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear, ["name"] = ColumnAccessLevel.Clear },
                hasUnconstrainedColumnAllow: true)));
        var service = new GovernedChangeDetectionService(
            repo, pdp, provider, Substitute.For<IColumnMaskingProvider>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(new ChangeDetectionOptions { MaxChangesPerCall = maxChanges }));
        return (service, provider, service.IssueCursor(Table, User(), Tenant, syncVersion: 1L));
    }

    private static Dictionary<string, object?> Key(int id) => new() { ["id"] = id };

    private static SourceChangeRecord Delete(int id, long version, string? tenant = "tenant-a", bool preImage = true) =>
        new(Key(id), CdcOperation.Delete, version, preImage ? new Dictionary<string, object?> { ["id"] = id, ["tenant_id"] = tenant } : null);

    [Fact]
    public async Task ChangesSince_KnownKeyOfOtherTenant_NotInRemoved()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(7, 2, tenant: "tenant-b"));
        var (service, _, cursor) = Setup(store, Customers());

        var result = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant, KnownKeys: [Key(7)]));

        result.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangesSince_DeleteWithVisiblePreImage_InRemoved()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(7, 2));
        var (service, _, cursor) = Setup(store, Customers());

        var result = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant));

        result.Removed.Select(r => Convert.ToInt32(r["id"])).ShouldBe([7]);
    }

    [Fact]
    public async Task ChangesSince_AllDeletedKeysOnTenantedTable_OnlyOwnTenant()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(1, 2, tenant: "tenant-a"));
        store.RecordChange(Table, Delete(2, 3, tenant: "tenant-b"));
        store.RecordChange(Table, Delete(3, 4, preImage: false));
        var (service, _, cursor) = Setup(store, Customers(policy: DeletedKeyPolicy.AllDeletedKeys));

        var result = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant));

        result.Removed.Select(r => Convert.ToInt32(r["id"])).ShouldBe([1]);
    }

    [Fact]
    public async Task ChangesSince_NullTenantRow_NotUpserted()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, new SourceChangeRecord(Key(5), CdcOperation.Insert, 2));
        store.SetCurrentRows(Table, [new Dictionary<string, object?> { ["id"] = 5, ["name"] = "shared", ["tenant_id"] = null }]);
        var (service, _, cursor) = Setup(store, Customers());

        var result = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant));

        result.Upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangesSince_ScopedTableWithoutPreImages_NotSupported()
    {
        var (service, _, cursor) = Setup(new NoChangeLogVersionProvider(), Customers());

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant)));

        ex.ErrorCode.ShouldBe(ChangeDetectionErrorCodes.ChangeTrackingNotSupported);
    }

    [Fact]
    public async Task ChangesSince_KnownKeys_NarrowRemovedList()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(1, 2));
        store.RecordChange(Table, Delete(2, 3));
        var (service, _, cursor) = Setup(store, Customers());

        var result = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant, KnownKeys: [Key(2)]));

        result.Removed.Select(r => Convert.ToInt32(r["id"])).ShouldBe([2]);
    }

    // ---- VF-13 ----

    [Fact]
    public async Task ChangesSince_DefaultProvider_NotSupported()
    {
        var (service, _, cursor) = Setup(new NoChangeLogVersionProvider(), Customers(tenanted: false));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant)));

        ex.ErrorCode.ShouldBe(ChangeDetectionErrorCodes.ChangeTrackingNotSupported);
    }

    [Fact]
    public async Task ChangesSince_OverCap_HasMoreAndCursorAtLastCompleteVersion()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(1, 2));
        store.RecordChange(Table, Delete(2, 3));
        store.RecordChange(Table, Delete(3, 4));
        var (service, _, cursor) = Setup(store, Customers(), maxChanges: 2);

        var first = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant));

        first.HasMore.ShouldBeTrue();
        first.SyncVersion.ShouldBe(3L);
        first.Removed.Select(r => Convert.ToInt32(r["id"])).ShouldBe([1, 2]);

        var second = await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, first.NextCursor, User(), Tenant));

        second.HasMore.ShouldBeFalse();
        second.Removed.Select(r => Convert.ToInt32(r["id"])).ShouldBe([3]);
    }

    [Fact]
    public async Task ChangesSince_SingleVersionOverCap_NotSupported()
    {
        var store = new InMemoryChangeVersionStore();
        store.RecordChange(Table, Delete(1, 2));
        store.RecordChange(Table, Delete(2, 2));
        store.RecordChange(Table, Delete(3, 2));
        var (service, _, cursor) = Setup(store, Customers(), maxChanges: 2);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant)));

        ex.ErrorCode.ShouldBe(ChangeDetectionErrorCodes.ChangeTrackingNotSupported);
    }

    [Fact]
    public async Task ChangesSince_ProviderReceivesCapPlusOne()
    {
        var provider = Substitute.For<IChangeVersionProvider>();
        provider.ProvidesPreImages.Returns(true);
        provider.GetCurrentVersionAsync(Table, Arg.Any<CancellationToken>()).Returns(new ValueTask<long?>(5L));
        provider.GetMinValidVersionAsync(Table, Arg.Any<CancellationToken>()).Returns(new ValueTask<long?>(0L));
        provider.GetChangesBetweenAsync(Table, Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SourceChangeRecord>>(Array.Empty<SourceChangeRecord>()));
        var (service, _, cursor) = Setup(provider, Customers(), maxChanges: 10);

        await service.GetChangesSinceAsync(new ChangesSinceRequest(Table, cursor, User(), Tenant));

        await provider.Received(1).GetChangesBetweenAsync(Table, 1L, 5L, 11, Arg.Any<CancellationToken>());
    }
}
