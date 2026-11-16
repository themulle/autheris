namespace Autheris.Tests.Integration;

using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// A restrictive catalog write inside a write scope (here: deactivating a table) must bump the table epoch and the catalog version
/// before the scope ends, while additive writes of the same scope are coalesced into the end-of-scope flush.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogWriteScopePostgreSqlTests : IClassFixture<PostgreSqlContainerFixture>
{
    private readonly PostgreSqlContainerFixture _fixture;

    public CatalogWriteScopePostgreSqlTests(PostgreSqlContainerFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
        builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogWriteScopePostgreSqlTests"));
        builder.UseSetting("Gateway:DataSources:Connections:warehouse:Provider", "PostgreSql");
        builder.UseSetting("Gateway:DataSources:Connections:warehouse:ConnectionString", _fixture.ConnectionString);
        builder.UseSetting("Gateway:DataSources:Connections:warehouse:DefaultSchema", "public");
    });

    private static TableMetadata Meta(string name, bool active)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("warehouse", "public", name),
            Table = new Table
            {
                Id = id,
                SourceType = "PostgreSql",
                SourceName = "warehouse",
                SchemaName = "public",
                TableName = name,
                DisplayName = name,
                DataSourceType = DataSourceType.Sql,
                IsActive = active
            },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" }]
        };
    }

    [Fact]
    public async Task HiddenFlagChangeInScope_InvalidatesDecisionCacheBeforeScopeEnds()
    {
        using var host = CreateHost();
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var epochs = scope.ServiceProvider.GetRequiredService<IPolicyEpochRepository>();
        var factory = scope.ServiceProvider.GetRequiredService<ICatalogWriteScopeFactory>();

        var secret = Meta("scope_secret", active: true);
        await repo.UpsertTableMetadataAsync(secret);
        var epochBefore = await epochs.GetTableEpochAsync(secret.Identifier);
        var versionBefore = await repo.GetCatalogVersionAsync();

        await using (factory.Begin("integration-test"))
        {
            // Additive write: its own epoch is committed, the version bump waits for the end of the scope.
            var additive = Meta("scope_additive", active: true);
            await repo.UpsertTableMetadataAsync(additive);
            (await repo.GetCatalogVersionAsync()).ShouldBe(versionBefore);
            (await epochs.GetTableEpochAsync(additive.Identifier)).ShouldBeGreaterThan(0);

            // Restrictive write (deactivation, the hidden state): epoch and catalog version move before the scope ends.
            await repo.UpsertTableMetadataAsync(secret with { Table = secret.Table with { IsActive = false } });

            (await epochs.GetTableEpochAsync(secret.Identifier)).ShouldBeGreaterThan(epochBefore);
            var versionDuring = await repo.GetCatalogVersionAsync();
            (versionDuring.StructureVersion + versionDuring.ContentVersion)
                .ShouldBeGreaterThan(versionBefore.StructureVersion + versionBefore.ContentVersion);
        }
    }
}
