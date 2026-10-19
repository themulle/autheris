namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 WP-1 + WP-2 end to end on SQLite: persisted tiers (real repository overlay) are visible only on the Discovery surface of the MCP
/// catalog, never on the DataAccess default (F-C13, G2), and a restriction is effective on the next read.
/// </summary>
public sealed class CatalogTierPersistenceEndToEndTests : IDisposable
{
    private static readonly Sid Admin = new("S-1-5-21-admin");
    private static readonly TableIdentifier Hidden = new("shared", "public", "hidden_t");
    private static readonly TableIdentifier Summary = new("shared", "public", "summary_t");
    private static readonly TableIdentifier Full = new("shared", "public", "full_t");
    private readonly SqliteGovernanceRepository _repo = Autheris.Tests.Unit.GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>());

    private static readonly IOptions<GatewayOptions> Shared = Options.Create(new GatewayOptions
    {
        Catalog = new DataCatalogOptions { SharedTables = [Hidden.ToString(), Summary.ToString(), Full.ToString()] }
    });

    public void Dispose() => _repo.Dispose();

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim("sub", "S-1-USER"), new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid", "S-1-USER"), new Claim("tenant_id", "tenant-a")], "test"));

    private async Task SeedAsync(TableIdentifier id, DatasetVisibility visibility)
    {
        await _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true },
            Columns = [new TableColumn { ColumnName = "secret_col", DataType = "TEXT" }]
        });
        await _repo.SetDatasetVisibilityAsync(id, visibility, Admin);
    }

    private McpDatasetCatalog Catalog()
    {
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        return new McpDatasetCatalog(_repo, consents, Substitute.For<IGatewayExecutionService>(), options: Shared,
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(_repo, consents, Shared.Value));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PersistedTiers_AreListedOnDiscovery_NeverOnDataAccess_AndRestrictionsApplyImmediately()
    {
        await SeedAsync(Hidden, DatasetVisibility.Hidden);
        await SeedAsync(Summary, DatasetVisibility.Summary);
        await SeedAsync(Full, DatasetVisibility.Full);
        var user = User();

        var listed = await Catalog().ListDatasetsAsync(user);

        listed.Datasets.Select(d => d.Id).Order().ToArray().ShouldBe([Full.ToString(), Summary.ToString()]);
        listed.Datasets.Single(d => d.Id == Summary.ToString()).ColumnCount.ShouldBe(0);
        listed.Datasets.Single(d => d.Id == Full.ToString()).ColumnCount.ShouldBe(1);

        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        var all = await _repo.GetAllTablesAsync();
        (await McpCatalogVisibility.VisibleTablesAsync(all, user, consents, Shared.Value, CancellationToken.None)).ShouldBeEmpty();
        (await CatalogVisibility.VisibleTablesAsync(all, user, new TenantId("tenant-a"), consents, null, TenancyMode.Multi, Shared.Value.Catalog.SharedTables)).ShouldBeEmpty();

        await _repo.SetDatasetVisibilityAsync(Full, DatasetVisibility.Hidden, Admin);
        (await Catalog().ListDatasetsAsync(user)).Datasets.Select(d => d.Id).ShouldBe([Summary.ToString()]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CorruptStoredTier_NeverListsTheTable_OnDiscovery()
    {
        await SeedAsync(Full, DatasetVisibility.Full);
        var meta = (await _repo.GetTableMetadataAsync(Full))!;
        await using (var connection = await _repo.Connections.OpenAsync(CancellationToken.None))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"UPDATE TABLE_CATALOG_SETTINGS SET visibility = 7 WHERE table_id = '{meta.Table.Id}'";
            await cmd.ExecuteNonQueryAsync();
        }

        (await Catalog().ListDatasetsAsync(User())).Datasets.ShouldBeEmpty();
    }
}
