namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogObjectsCursorSecurityIntegrationTests : IClassFixture<CatalogObjectsCursorSecurityIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private readonly HostFixture _fixture;

    public CatalogObjectsCursorSecurityIntegrationTests(HostFixture fixture) => _fixture = fixture;

    private async Task SeedAsync()
    {
        await _fixture.Gate.WaitAsync();
        try
        {
            if (_fixture.Seeded)
            {
                return;
            }

            using var scope = _fixture.Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

            for (var i = 0; i < 12; i++)
            {
                var id = new TableIdentifier("dom_cursor", "main", $"visible_{i:D2}");
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table
                    {
                        Id = Guid.NewGuid(),
                        DisplayName = id.TableName,
                        TableName = id.TableName,
                        SchemaName = id.Schema,
                        SourceType = "PostgreSql",
                        SourceName = id.Domain
                    },
                    Identifier = id,
                    Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
                });
                var meta = await repo.GetTableMetadataAsync(id);
                await repo.CreateConsentAsync(new Consent
                {
                    TableId = meta!.Table.Id,
                    TableIdentifier = id,
                    TenantId = new TenantId(TenantA),
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("S-ALICE"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
                });
            }

            using var admin = Client("S-CURSOR-ADMIN", TenantA, roles: "GovernanceAdmin", scopes: null);
            foreach (var hiddenName in new[] { "hidden_secret_a", "hidden_secret_b" })
            {
                var hiddenId = new TableIdentifier("dom_cursor", "main", hiddenName);
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table
                    {
                        Id = Guid.NewGuid(),
                        DisplayName = hiddenId.TableName,
                        TableName = hiddenId.TableName,
                        SchemaName = hiddenId.Schema,
                        SourceType = "PostgreSql",
                        SourceName = hiddenId.Domain
                    },
                    Identifier = hiddenId,
                    Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
                });

                if (string.Equals(hiddenName, "hidden_secret_a", StringComparison.Ordinal))
                {
                    var hiddenMetaBeforeHide = await repo.GetTableMetadataAsync(hiddenId);
                    await repo.CreateConsentAsync(new Consent
                    {
                        TableId = hiddenMetaBeforeHide!.Table.Id,
                        TableIdentifier = hiddenId,
                        TenantId = new TenantId(TenantA),
                        Effect = ConsentEffect.Allow,
                        GranteeType = GranteeType.User,
                        GranteeSid = new Sid("S-ALICE"),
                        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
                    });
                }

                using var hideResponse = await admin.PutAsJsonAsync(
                    $"/api/v1/catalog/datasets/{hiddenId}/discovery",
                    new { hidden = true });
                hideResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

                var hiddenMeta = await repo.GetTableMetadataAsync(hiddenId);
                hiddenMeta.ShouldNotBeNull();
                hiddenMeta!.Table.DiscoveryHidden.ShouldBeTrue();
            }

            var snapshotProvider = scope.ServiceProvider.GetService<ICatalogSnapshotProvider>();
            if (snapshotProvider is not null)
            {
                var snapshot = await snapshotProvider.GetSnapshotAsync();
                foreach (var hiddenName in new[] { "hidden_secret_a", "hidden_secret_b" })
                {
                    var hiddenId = new TableIdentifier("dom_cursor", "main", hiddenName);
                    var snapTable = snapshot.FindTable(hiddenId);
                    snapTable.ShouldNotBeNull();
                    snapTable!.Table.DiscoveryHidden.ShouldBeTrue();
                }
            }

            var tenantBId = new TableIdentifier("dom_b_cursor", "main", "tenant_b_only");
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = tenantBId.TableName,
                    TableName = tenantBId.TableName,
                    SchemaName = tenantBId.Schema,
                    SourceType = "PostgreSql",
                    SourceName = tenantBId.Domain
                },
                Identifier = tenantBId,
                Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
            });
            var metaB = await repo.GetTableMetadataAsync(tenantBId);
            await repo.CreateConsentAsync(new Consent
            {
                TableId = metaB!.Table.Id,
                TableIdentifier = tenantBId,
                TenantId = new TenantId(TenantB),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-ALICE"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
            });

            _fixture.Seeded = true;
        }
        finally
        {
            _fixture.Gate.Release();
        }
    }

    private HttpClient Client(string sid, string tenant, string roles = "Reader", string? scopes = "Autheris.Catalog.Read")
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        if (scopes is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        }

        return client;
    }

    [Fact]
    public async Task Cursor_ReplayAcrossCallers_Is400()
    {
        await SeedAsync();
        var alice = Client("S-ALICE", TenantA);
        var page1 = await alice.GetAsync("/api/v1/catalog/objects?pageSize=4");
        page1.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var cursor = doc1.RootElement.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        var bob = Client("S-BOB", TenantA);
        var replay = await bob.GetAsync($"/api/v1/catalog/objects?pageSize=4&cursor={Uri.EscapeDataString(cursor!)}");
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await replay.Content.ReadAsStringAsync();
        body.ShouldContain("INVALID_CURSOR");
        body.ShouldNotContain(cursor!);
    }

    [Fact]
    public async Task Cursor_ReplayAcrossTenants_Is400()
    {
        await SeedAsync();
        var aliceTenantA = Client("S-ALICE", TenantA);
        var page1 = await aliceTenantA.GetAsync("/api/v1/catalog/objects?pageSize=4");
        page1.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var cursor = doc1.RootElement.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        var aliceTenantB = Client("S-ALICE", TenantB);
        var replay = await aliceTenantB.GetAsync($"/api/v1/catalog/objects?pageSize=4&cursor={Uri.EscapeDataString(cursor!)}");
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await replay.Content.ReadAsStringAsync();
        body.ShouldContain("INVALID_CURSOR");
        body.ShouldNotContain(cursor!);
    }

    [Fact]
    public async Task Cursor_PagesOverLargeCatalog_NoHiddenObjectLeak()
    {
        await SeedAsync();
        var alice = Client("S-ALICE", TenantA);

        foreach (var accessQuery in new[] { string.Empty, "&accessStatus=granted" })
        {
            var collected = new List<string>();
            string? cursor = null;
            do
            {
                var url = cursor is null
                    ? $"/api/v1/catalog/objects?domain=dom_cursor&pageSize=4{accessQuery}"
                    : $"/api/v1/catalog/objects?domain=dom_cursor&pageSize=4{accessQuery}&cursor={Uri.EscapeDataString(cursor)}";
                var response = await alice.GetAsync(url);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                var raw = await response.Content.ReadAsStringAsync();
                raw.ShouldNotContain("hidden_secret_");

                using var doc = JsonDocument.Parse(raw);
                doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(12);
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    collected.Add(item.GetProperty("objectId").GetString()!);
                }

                cursor = doc.RootElement.TryGetProperty("nextCursor", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                    ? nextProp.GetString()
                    : null;
            }
            while (cursor is not null);

            collected.Count.ShouldBe(12);
            collected.Distinct(StringComparer.Ordinal).Count().ShouldBe(12);
            collected.ShouldAllBe(id => id.StartsWith("dom_cursor.main.visible_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SeededHiddenTables_PersistDiscoveryHidden_AndAdminReadsThemAsHidden()
    {
        await SeedAsync();

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var snapshotProvider = scope.ServiceProvider.GetService<ICatalogSnapshotProvider>();
            var snapshot = snapshotProvider is not null ? await snapshotProvider.GetSnapshotAsync() : null;

            foreach (var hiddenName in new[] { "hidden_secret_a", "hidden_secret_b" })
            {
                var hiddenId = new TableIdentifier("dom_cursor", "main", hiddenName);
                var settings = await repo.GetDatasetCatalogSettingsAsync(hiddenId, strict: true);
                settings.ShouldNotBeNull();
                settings!.DiscoveryHidden.ShouldBeTrue();

                var meta = await repo.GetTableMetadataAsync(hiddenId);
                meta.ShouldNotBeNull();
                meta!.Table.DiscoveryHidden.ShouldBeTrue();

                if (snapshot is not null)
                {
                    var snapTable = snapshot.FindTable(hiddenId);
                    snapTable.ShouldNotBeNull();
                    snapTable!.Table.DiscoveryHidden.ShouldBeTrue();
                }
            }
        }

        using var admin = Client("S-CURSOR-ADMIN", TenantA, roles: "GovernanceAdmin");
        var response = await admin.GetAsync("/api/v1/catalog/objects?domain=dom_cursor&pageSize=20");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(14);

        var hiddenByObjectId = doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("objectId").GetString()!,
                static item => item.GetProperty("hidden").GetBoolean(),
                StringComparer.Ordinal);

        hiddenByObjectId.Count.ShouldBe(14);
        hiddenByObjectId["dom_cursor.main.hidden_secret_a"].ShouldBeTrue();
        hiddenByObjectId["dom_cursor.main.hidden_secret_b"].ShouldBeTrue();
        for (var i = 0; i < 12; i++)
        {
            hiddenByObjectId[$"dom_cursor.main.visible_{i:D2}"].ShouldBeFalse();
        }
    }

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Seeded { get; set; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", TenantA);
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:Cluster:Secret", "integration-catalog-cursor-secret-key-0123456789abcdef");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogObjectsCursorSecurity"));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            Gate.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
