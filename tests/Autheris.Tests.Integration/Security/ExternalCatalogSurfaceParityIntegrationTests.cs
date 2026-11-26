namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// #1063 §1, #1088: end-to-end visibility parity of one Reader of the catalog owner tenant across every external catalog surface: REST
/// <c>/api/v1/catalog/objects</c>, <c>/datasets</c>, <c>/search</c>, <c>/objects/facets</c>, <c>/objects/{id}</c>, <c>/datasets/{id}</c>,
/// GraphQL <c>catalog</c> / <c>catalogObject</c> and the MCP tools <c>list_datasets</c> and <c>search_catalog</c>; <c>describe_dataset</c>
/// describes every catalog-visible table (withholding value-bearing metadata without a data grant, issue #1150) while <c>get_table_schema</c>
/// additionally needs a data grant, and neither ever describes a catalog-invisible table. The hidden flag is set through the dataset catalog admin endpoint (the only writer of discovery flags, persisted in
/// <c>TABLE_CATALOG_SETTINGS</c>) and ownership through container assignment (the container owner group), so the hidden-object and owner paths
/// are exercised for real rather than through a data grant.
/// </summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogSurfaceParityIntegrationTests : IClassFixture<ExternalCatalogSurfaceParityIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserSid = "S-1-PARITY-INT-USER";
    private const string TenantBUserSid = "S-1-PARITY-INT-B-USER";
    private const string AdminSid = "S-1-PARITY-INT-GOVADMIN";
    private static readonly Sid OtherOwner = new("S-1-PARITY-INT-OTHER-OWNER");

    // Every table name starts with a distinct word, so no name is a substring of another and a text match on one name is unambiguous.

    /// <summary>Open container of the caller's tenant: visible without any grant.</summary>
    private static readonly TableIdentifier OpenTable = new("dom_parity", "main", "alpha_open_orders");

    /// <summary>Unassigned, with a data grant (Allow consent) of the caller: visible.</summary>
    private static readonly TableIdentifier ConsentTable = new("dom_parity", "main", "bravo_consent_orders");

    /// <summary>OnRequest container on which the caller holds a catalog grant: visible.</summary>
    private static readonly TableIdentifier CatalogGrantTable = new("dom_parity", "main", "charlie_catgrant_orders");

    /// <summary>Hidden, in an OnRequest container whose owner group is the caller, without any grant: visible (owner path).</summary>
    private static readonly TableIdentifier HiddenOwnedTable = new("dom_parity", "main", "delta_hidden_owned_orders");

    /// <summary>Hidden, in an Open container owned by another group: invisible (the hide beats Open).</summary>
    private static readonly TableIdentifier HiddenOpenTable = new("dom_parity", "main", "echo_hidden_open_orders");

    /// <summary>Hidden, not owned, with a data grant of the caller: invisible (a data grant never lifts a discovery hide).</summary>
    private static readonly TableIdentifier HiddenConsentTable = new("dom_parity", "main", "foxtrot_hidden_consent_orders");

    /// <summary>OnRequest container owned by another group, no grant: invisible.</summary>
    private static readonly TableIdentifier OnRequestTable = new("dom_parity", "main", "golf_onrequest_orders");

    /// <summary>No container (the system container Unassigned, OnRequest), no grant: invisible.</summary>
    private static readonly TableIdentifier UnassignedTable = new("dom_parity", "main", "hotel_unassigned_orders");

    /// <summary>Published in an Open container of tenant B only (with a tenant-B grant): invisible to tenant A.</summary>
    private static readonly TableIdentifier ForeignTable = new("dom_b", "main", "india_foreign_orders");

    /// <summary>Semantic virtual table owned by tenant B, even with a (stray) tenant-A data grant of the caller: invisible.</summary>
    private static readonly TableIdentifier ForeignVirtualTable =
        new(SemanticVirtualTables.Domain, SemanticVirtualTables.TenantSchema(new TenantId(TenantB)), "juliet_foreign_virtual_orders");

    private static readonly TableIdentifier[] AllTables =
    [
        OpenTable, ConsentTable, CatalogGrantTable, HiddenOwnedTable, HiddenOpenTable, HiddenConsentTable,
        OnRequestTable, UnassignedTable, ForeignTable, ForeignVirtualTable
    ];

    private static readonly TableIdentifier[] HiddenTables = [HiddenOwnedTable, HiddenOpenTable, HiddenConsentTable];

    private static readonly string[] ExpectedVisible =
        new[] { OpenTable, ConsentTable, CatalogGrantTable, HiddenOwnedTable }
            .Select(static t => t.TableName)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// <c>get_table_schema</c> is decided by the Access Decision Point like every data tool and needs a data grant in addition to catalog
    /// visibility, so it shows only the visible tables with a data grant. <c>describe_dataset</c> describes every catalog-visible table
    /// (<see cref="ExpectedVisible"/>), reporting <c>REQUEST_ACCESS</c> and withholding value-bearing metadata when no data grant is held (#1150).
    /// </summary>
    private static readonly string[] ExpectedSchemaGrantOnly = [ConsentTable.TableName];

    private const string NotVisibleAnswer = "\"error\":{\"code\":\"NOT_FOUND\",\"message\":\"Dataset not found or not visible.\"}";

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
                // Tenant A is the catalog owner: tables without an owner tenant belong to it, so its container owner groups own them.
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", TenantA);
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("SurfaceParity"));
                b.UseSetting("Gateway:Mcp:Enabled", "true");
                b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
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

    private readonly HostFixture _fixture;

    public ExternalCatalogSurfaceParityIntegrationTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EveryExternalCatalogSurface_ShowsExactlyTheSameTables_ForHiddenOwnerContainerAndForeignTenantCases()
    {
        await EnsureSeededAsync();
        using var client = CreateClient(UserSid, TenantA, "Reader");

        var surfaces = await CollectVisibleTablesPerSurfaceAsync(client);

        var mismatches = surfaces
            .Where(static s => !s.Value.SequenceEqual(ExpectedVisible, StringComparer.Ordinal))
            .Select(static s => $"{s.Key}: [{string.Join(", ", s.Value)}]")
            .ToList();
        mismatches.ShouldBeEmpty($"expected on every surface: [{string.Join(", ", ExpectedVisible)}]");
    }

    [Fact]
    public async Task McpSchemaTools_DescribeOnlyCatalogVisibleTablesWithADataGrant_AndAnswerInvisibleTablesLikeMissingOnes()
    {
        await EnsureSeededAsync();
        using var client = CreateClient(UserSid, TenantA, "Reader");
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var missing = new TableIdentifier("dom_parity", "main", "kilo_missing_orders");

        foreach (var tool in new[] { "describe_dataset", "get_table_schema" })
        {
            var described = new List<string>();
            var (missingError, missingText) = await McpTestClient.CallAsync(mcp, tool, new { dataset = missing.ToString() });
            var missingAnswer = (missingError, missingText.Replace(missing.ToString(), "<dataset>", StringComparison.Ordinal));

            foreach (var table in AllTables)
            {
                var (isError, text) = await McpTestClient.CallAsync(mcp, tool, new { dataset = table.ToString() });
                if (IsDescribed(tool, table, isError, text))
                {
                    described.Add(table.TableName);
                    if (tool == "describe_dataset")
                    {
                        using var doc = JsonDocument.Parse(text);
                        var expectedStatus = table.Equals(ConsentTable) ? "GRANTED" : "REQUEST_ACCESS";
                        doc.RootElement.GetProperty("accessStatus").GetString().ShouldBe(expectedStatus, table.ToString());
                    }
                }

                if (!ExpectedVisible.Contains(table.TableName, StringComparer.Ordinal))
                {
                    // Without a data grant the Access Decision Point answers exactly like for a missing dataset. A caller that holds a data
                    // grant on an invisible table (and so already knows it) gets the fixed not-found answer (describe_dataset) or an empty
                    // schema list (get_table_schema). None of the answers describes the table.
                    var answer = (isError, text.Replace(table.ToString(), "<dataset>", StringComparison.Ordinal));
                    (answer == missingAnswer ||
                     (isError && text.Contains(NotVisibleAnswer, StringComparison.Ordinal)) ||
                     (!isError && tool == "get_table_schema" && IsEmptySchemaList(text)))
                        .ShouldBeTrue($"{tool} {table}: {text}");
                }
            }

            var expected = tool == "describe_dataset" ? ExpectedVisible : ExpectedSchemaGrantOnly;
            Names(described).ShouldBe(expected, tool);
        }
    }

    [Fact]
    public async Task InvisibleTables_AnswerLikeAMissingTable_OnRestDetailReads()
    {
        await EnsureSeededAsync();
        using var client = CreateClient(UserSid, TenantA, "Reader");
        var missing = new TableIdentifier("dom_parity", "main", "kilo_missing_orders");

        using var missingObject = await client.GetAsync($"/api/v1/catalog/objects/{missing}");
        missingObject.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var missingShape = ProblemShape(await missingObject.Content.ReadAsStringAsync());

        foreach (var table in AllTables.Where(static t => !ExpectedVisible.Contains(t.TableName, StringComparer.Ordinal)))
        {
            using var objectResponse = await client.GetAsync($"/api/v1/catalog/objects/{table}");
            objectResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound, table.ToString());
            ProblemShape(await objectResponse.Content.ReadAsStringAsync()).ShouldBe(missingShape, table.ToString());

            using var datasetResponse = await client.GetAsync($"/api/v1/catalog/datasets/{table}");
            datasetResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound, table.ToString());
        }
    }

    [Fact]
    public async Task SeededState_IsReal_AdminSeesTheHiddenTablesAsHidden_AndTenantBSeesItsOwnTable()
    {
        await EnsureSeededAsync();

        // The hidden flag is persisted and the tables exist: the catalog owner's GovernanceAdmin reads them, flagged hidden.
        using var admin = CreateClient(AdminSid, TenantA, "GovernanceAdmin");
        foreach (var table in HiddenTables)
        {
            using var doc = await GetJsonAsync(admin, $"/api/v1/catalog/objects/{table}");
            doc.RootElement.GetProperty("hidden").GetBoolean().ShouldBeTrue(table.ToString());
        }

        // The foreign table is invisible to tenant A because of the tenant boundary, not because it is unreachable.
        using var tenantB = CreateClient(TenantBUserSid, TenantB, "Reader");
        (await tenantB.GetAsync($"/api/v1/catalog/objects/{ForeignTable}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<SortedDictionary<string, string[]>> CollectVisibleTablesPerSurfaceAsync(HttpClient client)
    {
        var surfaces = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        // REST listings.
        using (var doc = await GetJsonAsync(client, "/api/v1/catalog/objects?pageSize=100"))
        {
            surfaces["rest:objects"] = Names(doc.RootElement.GetProperty("items").EnumerateArray().Select(static e => e.GetProperty("objectId").GetString()!));
        }

        using (var doc = await GetJsonAsync(client, "/api/v1/catalog/datasets"))
        {
            surfaces["rest:datasets"] = Names(doc.RootElement.EnumerateArray().Select(static e => e.GetProperty("datasetId").GetString()!));
        }

        using (var doc = await GetJsonAsync(client, "/api/v1/catalog/search?q=orders&mode=keyword"))
        {
            surfaces["rest:search"] = Names(doc.RootElement.EnumerateArray().Select(ExtractSearchId));
        }

        using (var doc = await GetJsonAsync(client, "/api/v1/catalog/objects/facets"))
        {
            // Facets carry no names: the count must equal the number of visible tables.
            var count = doc.RootElement.GetProperty("totalCount").GetInt32();
            surfaces["rest:objects/facets(totalCount)"] = count == ExpectedVisible.Length ? ExpectedVisible : [$"totalCount={count}"];
        }

        // REST detail reads.
        var objectDetail = new List<string>();
        var datasetDetail = new List<string>();
        foreach (var table in AllTables)
        {
            using (var response = await client.GetAsync($"/api/v1/catalog/objects/{table}"))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    objectDetail.Add(table.TableName);
                }
            }

            using (var response = await client.GetAsync($"/api/v1/catalog/datasets/{table}"))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    datasetDetail.Add(table.TableName);
                }
            }
        }

        surfaces["rest:objects/{id}"] = Names(objectDetail);
        surfaces["rest:datasets/{id}"] = Names(datasetDetail);

        // GraphQL.
        using (var doc = await PostGraphQlAsync(client, "query { catalog(first: 20) { tableName } }"))
        {
            surfaces["graphql:catalog"] = Names(doc.RootElement.GetProperty("data").GetProperty("catalog").EnumerateArray()
                .Select(static e => e.GetProperty("tableName").GetString()!));
        }

        using (var doc = await PostGraphQlAsync(client, "query { catalog(first: 20, search: \"orders\") { tableName } }"))
        {
            surfaces["graphql:catalog(search)"] = Names(doc.RootElement.GetProperty("data").GetProperty("catalog").EnumerateArray()
                .Select(static e => e.GetProperty("tableName").GetString()!));
        }

        var graphQlDetail = new List<string>();
        foreach (var table in AllTables)
        {
            var query = $"query {{ catalogObject(domain: \"{table.Domain}\", schema: \"{table.Schema}\", name: \"{table.TableName}\") {{ tableName }} }}";
            using var response = await client.PostAsJsonAsync("/graphql", new { query });
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("catalogObject", out var obj) && obj.ValueKind == JsonValueKind.Object)
            {
                graphQlDetail.Add(obj.GetProperty("tableName").GetString()!);
            }
        }

        surfaces["graphql:catalogObject"] = Names(graphQlDetail);

        // MCP.
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (listError, list) = await McpTestClient.CallJsonAsync(mcp, "list_datasets", new { });
        listError.ShouldBeFalse(list.ToString());
        surfaces["mcp:list_datasets"] = Names(list.GetProperty("datasets").EnumerateArray().Select(static d => d.GetProperty("id").GetString()!));

        var (searchError, searchText) = await McpTestClient.CallAsync(mcp, "search_catalog", new { query = "orders" });
        searchError.ShouldBeFalse(searchText);
        surfaces["mcp:search_catalog"] = Names(AllTables.Select(static t => t.TableName).Where(n => searchText.Contains(n, StringComparison.Ordinal)));

        return surfaces;
    }

    private static bool IsEmptySchemaList(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array && tables.GetArrayLength() == 0;
    }

    private static bool IsDescribed(string tool, TableIdentifier table, bool isError, string text)
    {
        if (isError || !text.TrimStart().StartsWith('{'))
        {
            return false;
        }

        if (tool == "describe_dataset")
        {
            return text.Contains(table.TableName, StringComparison.Ordinal);
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array && tables.GetArrayLength() > 0;
    }

    private static string[] Names(IEnumerable<string> ids) =>
        ids.Select(static id => id.Contains('.', StringComparison.Ordinal) ? id[(id.LastIndexOf('.') + 1)..] : id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url}: {body}");
        return JsonDocument.Parse(body);
    }

    // Fails unless the call succeeded with a data payload, so an error body can never pass for an empty result.
    private static async Task<JsonDocument> PostGraphQlAsync(HttpClient client, string query)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue(body);
        var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out _).ShouldBeFalse(body);
        return doc;
    }

    /// <summary>The comparable part of a problem body: everything except per-request values (trace id, instance).</summary>
    private static string ProblemShape(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var parts = new List<string>();
        foreach (var name in new[] { "type", "title", "status", "detail", "code" })
        {
            parts.Add(doc.RootElement.TryGetProperty(name, out var value) ? $"{name}={value}" : $"{name}=-");
        }

        return string.Join('|', parts);
    }

    private static string ExtractSearchId(JsonElement entry)
    {
        if (entry.TryGetProperty("tableIdentifier", out var tid))
        {
            if (tid.ValueKind == JsonValueKind.String)
            {
                return tid.GetString()!;
            }

            if (tid.ValueKind == JsonValueKind.Object)
            {
                return $"{tid.GetProperty("domain").GetString()}.{tid.GetProperty("schema").GetString()}.{tid.GetProperty("tableName").GetString()}";
            }
        }

        return entry.GetProperty("datasetId").GetString()!;
    }

    private HttpClient CreateClient(string sid, string tenant, string roles)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    private async Task EnsureSeededAsync()
    {
        await _fixture.Gate.WaitAsync();
        try
        {
            if (_fixture.Seeded)
            {
                return;
            }

            await SeedAsync();
            _fixture.Seeded = true;
        }
        finally
        {
            _fixture.Gate.Release();
        }
    }

    private async Task SeedAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var domains = scope.ServiceProvider.GetRequiredService<IGovernanceDomainRepository>();
        var grants = scope.ServiceProvider.GetRequiredService<ICatalogVisibilityGrantRepository>();
        var tenantA = new TenantId(TenantA);
        var tenantB = new TenantId(TenantB);
        var user = new Sid(UserSid);

        var ids = new Dictionary<TableIdentifier, Guid>();
        foreach (var table in AllTables)
        {
            var isVirtual = table.Equals(ForeignVirtualTable);
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = table.TableName,
                    TableName = table.TableName,
                    SchemaName = table.Schema,
                    SourceType = "PostgreSql",
                    SourceName = table.Domain,
                    DocumentationSource = isVirtual ? SemanticVirtualTables.SourceMarker : null,
                    IsActive = true
                },
                Identifier = table,
                Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
            });
            ids[table] = (await repo.GetTableMetadataAsync(table))!.Table.Id;
        }

        // Containers of tenant A (the catalog owner); the caller is the owner group of exactly one container.
        await CatalogVisibilitySeeding.SeedOpenDomainAsync(domains, tenantA, OtherOwner, ids[OpenTable], ids[HiddenOpenTable]);
        await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, tenantA, user, ids[HiddenOwnedTable]);
        await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, tenantA, OtherOwner, ids[OnRequestTable], ids[HiddenConsentTable]);
        var grantedDomain = await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, tenantA, OtherOwner, ids[CatalogGrantTable]);
        await CatalogVisibilitySeeding.SeedCatalogGrantAsync(
            grants, tenantA, new ContainerRef(ContainerKind.Domain, grantedDomain.Id), user, OtherOwner);

        // Tenant B publishes its table in its own Open container and grants its own user.
        await CatalogVisibilitySeeding.SeedOpenDomainAsync(domains, tenantB, new Sid(TenantBUserSid), ids[ForeignTable]);
        await CreateAllowConsentAsync(repo, ForeignTable, ids[ForeignTable], tenantB, new Sid(TenantBUserSid));

        // Data grants of the caller: on a visible table, on a hidden table it does not own, and a stray one on tenant B's virtual table.
        await CreateAllowConsentAsync(repo, ConsentTable, ids[ConsentTable], tenantA, user);
        await CreateAllowConsentAsync(repo, HiddenConsentTable, ids[HiddenConsentTable], tenantA, user);
        await CreateAllowConsentAsync(repo, ForeignVirtualTable, ids[ForeignVirtualTable], tenantA, user);

        // The hidden flag goes through the dataset catalog admin endpoint (the product writer of discovery flags), as the catalog owner's GovernanceAdmin.
        using (var admin = CreateClient(AdminSid, TenantA, "GovernanceAdmin"))
        {
            foreach (var table in HiddenTables)
            {
                using var content = new StringContent("{\"hidden\":true}", Encoding.UTF8, "application/json");
                using var response = await admin.PutAsync($"/api/v1/catalog/datasets/{table}/discovery", content);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{table}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        var searchEngine = scope.ServiceProvider.GetRequiredService<ICatalogSearchEngine>();
        var indexed = new List<TableMetadata>(AllTables.Length);
        foreach (var table in AllTables)
        {
            var meta = (await repo.GetTableMetadataAsync(table))!;
            meta.Table.DiscoveryHidden.ShouldBe(HiddenTables.Contains(table), $"persisted hidden flag of {table}");
            indexed.Add(meta);
        }

        await searchEngine.RebuildIndexAsync(indexed);
    }

    private static Task CreateAllowConsentAsync(IGovernanceRepository repo, TableIdentifier table, Guid tableId, TenantId tenant, Sid grantee)
    {
        var now = DateTimeOffset.UtcNow;
        return repo.CreateConsentAsync(new Consent
        {
            TableId = tableId,
            TableIdentifier = table,
            TenantId = tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = grantee,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });
    }
}
