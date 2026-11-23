namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed class ExternalCatalogCursorTests
{
    private const string PersistentSigningKey = "01234567890123456789012345678901-external-catalog";

    private static ClaimsPrincipal Person(string sid = "S-1-5-21-CURSOR-USER", string tenant = "tenant-a") =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(tenant, sid, scopes: ExternalApiPermissions.CatalogRead);

    [Fact]
    public void EncodeAndDecode_RoundTripsKeyAndCount_WhenPersistentKeyIsConfigured()
    {
        var key = ExternalCatalogCursor.ResolvePersistentKey(
            new GatewayOptions { RestApi = new RestApiOptions { BulkStream = new BulkStreamOptions { SigningKey = PersistentSigningKey } } });
        key.ShouldNotBeNull();

        var user = Person();
        var query = new ExternalCatalogListQuery(Domain: "sales", Kind: "view", PageSize: 10);
        var binding = ExternalCatalogCursor.ComputeBinding(query, "view", null, null, new TenantId("tenant-a"), user);

        var encoded = ExternalCatalogCursor.Encode("sales.dbo.orders", 42, binding, key);
        ExternalCatalogCursor.TryDecode(encoded, binding, key, out var lastKey, out var count).ShouldBeTrue();
        lastKey.ShouldBe("sales.dbo.orders");
        count.ShouldBe(42);
    }

    [Fact]
    public async Task AppOnly_KeysetPaging_LoadsOwnersAndArtifactsForReturnedPageOnly()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.RegisterExternalSystem("tenant-a", "sync-app", enabled: true);

        for (var i = 0; i < 6; i++)
        {
            var tbl = harness.AddTable("pg", $"t_{i:00}", ownerTenantId: "tenant-a");
            harness.Owners.AssignOwner(tbl.Identifier, $"S-OWN-{i}", $"Owner {i}");
            harness.Artifacts.Artifacts[tbl.Table.Id] = new DiscoveryArtifacts(
                tbl.Table.Id, "tenant-a", "[{\"x\":1}]", null, "sha-" + i, "admin", ExternalCatalogTestHarness.FixedNow);
        }

        var countingArtifacts = new CountingArtifactStore(harness.Artifacts);
        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "sync-app", ExternalApiPermissions.CatalogRead);

        var reader = new ExternalCatalogReader(
            harness.Tables,
            new DiscoveryServicePlaceholder(),
            harness.Consents,
            harness.Owners,
            harness.WorkflowRepo,
            harness.ChangeStore,
            Microsoft.Extensions.Options.Options.Create(harness.GatewayOptions),
            countingArtifacts,
            harness.Clock);

        var page1 = await reader.ListObjectsAsync(app, new TenantId("tenant-a"), new ExternalCatalogListQuery(PageSize: 2));
        page1.IsSuccess.ShouldBeTrue();
        page1.Value!.TotalCount.ShouldBe(6);
        page1.Value.Items.Select(static x => x.ObjectId).ShouldBe(["pg.dbo.t_00", "pg.dbo.t_01"]);
        page1.Value.NextCursor.ShouldNotBeNull();
        countingArtifacts.Reads.ShouldBe(2);

        var page2 = await reader.ListObjectsAsync(app, new TenantId("tenant-a"), new ExternalCatalogListQuery(PageSize: 2, Cursor: page1.Value.NextCursor));
        page2.IsSuccess.ShouldBeTrue();
        page2.Value!.TotalCount.ShouldBe(6);
        page2.Value.Items.Select(static x => x.ObjectId).ShouldBe(["pg.dbo.t_02", "pg.dbo.t_03"]);
        countingArtifacts.Reads.ShouldBe(4);
    }

    [Fact]
    public async Task Endpoint_RejectsMalformedLegacyAndMismatchedCursorsWith400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        for (var i = 0; i < 4; i++)
        {
            harness.AddTable("pg", $"t_{i:00}");
        }

        var user = Person();
        var p1 = (await harness.ListObjectsAsync(user, pageSize: 2, domain: "pg")).ReadJson();
        var cursor = p1.GetProperty("nextCursor").GetString()!;

        (await harness.ListObjectsAsync(user, pageSize: 2, domain: "hr", cursor: cursor)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await harness.ListObjectsAsync(user, pageSize: 2, domain: "pg", cursor: "c1.Mg")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await harness.ListObjectsAsync(user, pageSize: 2, domain: "pg", cursor: "bad-cursor!")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    private sealed class CountingArtifactStore(IDiscoveryArtifactStore inner) : IDiscoveryArtifactStore
    {
        public int Reads { get; private set; }
        public System.Threading.Tasks.Task<DiscoveryArtifacts?> GetAsync(Guid tableId, System.Threading.CancellationToken ct = default)
        {
            Reads++;
            return inner.GetAsync(tableId, ct);
        }

        public System.Threading.Tasks.Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(Guid tableId, System.Threading.CancellationToken ct = default)
        {
            Reads++;
            return inner.GetAllAsync(tableId, ct);
        }

        public System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiscoveryArtifacts>>> GetAllForTablesAsync(
            IReadOnlyCollection<Guid> tableIds,
            System.Threading.CancellationToken ct = default)
        {
            Reads++;
            return inner.GetAllForTablesAsync(tableIds, ct);
        }

        public System.Threading.Tasks.Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, System.Threading.CancellationToken ct = default) =>
            inner.UpsertAsync(artifacts, actor, ct);
        public System.Threading.Tasks.Task<bool> DeleteAsync(Guid tableId, Sid actor, System.Threading.CancellationToken ct = default) =>
            inner.DeleteAsync(tableId, actor, ct);
    }

    private sealed class DiscoveryServicePlaceholder : Autheris.Application.Catalog.Discovery.IDiscoveryService
    {
        public ValueTask<Autheris.Application.Catalog.Discovery.DiscoveryPage> ListAsync(Autheris.Application.Catalog.Discovery.DiscoveryQuery query, Autheris.Application.Catalog.Interfaces.CatalogCaller caller, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Autheris.Application.Catalog.Discovery.DiscoveryKeysetPage> ListKeysetAsync(Autheris.Application.Catalog.Discovery.DiscoveryKeysetQuery query, Autheris.Application.Catalog.Interfaces.CatalogCaller caller, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Autheris.Application.Catalog.Discovery.DiscoveryFacets> FacetsAsync(DiscoveryAccessState? access, Autheris.Application.Catalog.Interfaces.CatalogCaller caller, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, Autheris.Application.Catalog.Interfaces.CatalogCaller caller, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, Autheris.Application.Catalog.Discovery.DiscoverySearchOptions options, Autheris.Application.Catalog.Interfaces.CatalogCaller caller, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
    }
}
