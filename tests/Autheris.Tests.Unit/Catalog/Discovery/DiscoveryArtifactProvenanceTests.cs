namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryArtifactProvenanceTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static (DiscoveryArtifactService Service, TableMetadata Table, FakeDiscoveryArtifactStore Store) CreateHarness()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: Tenant.Value,
            columns:
            [
                ("id", false, false),
                ("amount", false, false)
            ]);
        tables.Tables.Add(orders);

        var store = new FakeDiscoveryArtifactStore();
        var service = new DiscoveryArtifactService(
            tables,
            store,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings() }));

        return (service, orders, store);
    }

    [Fact]
    public async Task Upload_cannot_set_import()
    {
        var (service, table, store) = CreateHarness();
        var admin = DiscoveryTestBuilder.User("S-1-5-21-ADM-1", Tenant.Value, roles: ["GovernanceAdmin"]);
        var body = Encoding.UTF8.GetBytes("""
        {
          "schemaVersion": 1,
          "provenance": "IMPORT",
          "samples": [ { "id": 1, "amount": 99.5 } ]
        }
        """);

        var result = await service.UpsertAsync(admin, table.Identifier.ToString(), body, "rest", CancellationToken.None);

        result.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        var uploadRow = await store.GetAsync(table.Table.Id, ArtifactProvenance.Upload);
        var importRow = await store.GetAsync(table.Table.Id, ArtifactProvenance.Import);
        uploadRow.ShouldNotBeNull();
        uploadRow!.Provenance.ShouldBe(ArtifactProvenance.Upload);
        importRow.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_leaves_import()
    {
        var (service, table, store) = CreateHarness();
        var actor = new Sid("S-1-5-21-SYS-1");
        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id":1,"amount":10}]""",
            StatisticsJson: null,
            "sha-import",
            "dbt-ingestion",
            DateTimeOffset.UtcNow,
            SchemaVersion: 1,
            Provenance: ArtifactProvenance.Import), actor);

        await store.UpsertAsync(new DiscoveryArtifacts(
            table.Table.Id,
            Tenant.Value,
            """[{"id":2,"amount":20}]""",
            StatisticsJson: """{"table":{"approxRowCount":50}}""",
            "sha-upload",
            "user",
            DateTimeOffset.UtcNow,
            SchemaVersion: 1,
            Provenance: ArtifactProvenance.Upload), actor);

        var admin = DiscoveryTestBuilder.User("S-1-5-21-ADM-1", Tenant.Value, roles: ["GovernanceAdmin"]);
        var del = await service.DeleteAsync(admin, table.Identifier.ToString(), "rest", CancellationToken.None);

        del.Outcome.ShouldBe(DiscoveryArtifactOutcome.NoContent);
        (await store.GetAsync(table.Table.Id, ArtifactProvenance.Upload)).ShouldBeNull();
        var preserved = await store.GetAsync(table.Table.Id, ArtifactProvenance.Import);
        preserved.ShouldNotBeNull();
        preserved!.ContentSha256.ShouldBe("sha-import");
    }

    [Theory]
    [InlineData(null, ArtifactProvenance.Unknown)]
    [InlineData("", ArtifactProvenance.Unknown)]
    [InlineData("UNKNOWN", ArtifactProvenance.Unknown)]
    [InlineData("something_unexpected", ArtifactProvenance.Unknown)]
    [InlineData("import", ArtifactProvenance.Import)]
    [InlineData("IMPORT", ArtifactProvenance.Import)]
    [InlineData("upload", ArtifactProvenance.Upload)]
    [InlineData("UPLOAD", ArtifactProvenance.Upload)]
    public void Unknown_parsed_as_unknown(string? raw, ArtifactProvenance expected)
    {
        ArtifactProvenanceExtensions.Parse(raw).ShouldBe(expected);
    }
}
