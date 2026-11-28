using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using Table = Autheris.Domain.Model.Table;

namespace Autheris.Tests.Integration;

/// <summary>
/// Opt-in <c>extensions.autheris.roots</c> over the real host. <c>facts_consent</c> carries a real consent row filter,
/// <c>facts_cell</c> a per-cell consent, <c>air1</c> only a Virtual Filter and <c>facts_hidden</c> is not readable at all.
/// </summary>
public sealed class DatasetFactsExtensionIntegrationTests : IClassFixture<DatasetFactsExtensionIntegrationTests.FactsFixture>
{
    private const string ReadOnlyScope = "ReadOnlyTest";
    private const string ConsentQuery = "{ a: default_main_facts_consent(first: 20) { id } }";
    private static readonly string[] Constants =
    [
        "Row filter applies (defined by data owner).",
        "Row filter applies (depends on an attribute of the caller)."
    ];

    private readonly FactsFixture _fixture;

    public DatasetFactsExtensionIntegrationTests(FactsFixture fixture) => _fixture = fixture;

    private HttpClient Caller(string? scope = null, string? roles = null)
    {
        var client = _fixture.Client();
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Remove("X-Test-Roles");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }

        return client;
    }

    private static async Task<JsonDocument> GraphQlRawAsync(HttpClient client, string query, bool datasetFacts)
    {
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        object body = datasetFacts
            ? new { query, extensions = new { autheris = new { datasetFacts = true } } }
            : new { query };
        var response = await client.PostAsJsonAsync("/graphql", body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private Task<JsonDocument> GraphQlRawAsync(string query, bool datasetFacts) => GraphQlRawAsync(_fixture.Client(), query, datasetFacts);

    private static JsonElement? Roots(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("extensions", out var ext)
        && ext.TryGetProperty("autheris", out var autheris)
        && autheris.TryGetProperty("roots", out var roots)
            ? roots
            : null;

    private static string[] Descriptions(JsonElement entry) =>
        [.. entry.GetProperty("rowFilters").EnumerateArray().Select(f => f.GetProperty("description").GetString()!)];

    private static async Task<string[]> ActiveRowFiltersAsync(HttpClient client, string dataset)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "describe_my_access", new { dataset });
        isError.ShouldBeFalse(payload.ToString());
        payload.GetProperty("decision").GetString().ShouldBe("ALLOWED", payload.ToString());
        return [.. payload.GetProperty("activeRowFilters").EnumerateArray().Select(f => f.GetString()!)];
    }

    [Fact]
    public async Task GraphQl_WithFlag_ReportsConsentRowFilterAsFixedText()
    {
        using var doc = await GraphQlRawAsync(ConsentQuery, datasetFacts: true);

        var a = Roots(doc)!.Value.GetProperty("a");
        a.GetProperty("dataset").GetString().ShouldBe("default.main.facts_consent");
        a.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        Descriptions(a).ShouldAllBe(d => Constants.Contains(d));
    }

    [Fact]
    public async Task McpQueryGraphql_WithDatasetFacts_ReturnsSameRootsAsGraphQl()
    {
        using var graphQl = await GraphQlRawAsync(ConsentQuery, datasetFacts: true);
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = ConsentQuery, datasetFacts = true });

        isError.ShouldBeFalse(payload.ToString());
        payload.GetProperty("extensions").GetProperty("autheris").GetProperty("roots").GetRawText()
            .ShouldBe(Roots(graphQl)!.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_WithoutFlag_HasNoAutherisRoots()
    {
        using var doc = await GraphQlRawAsync(ConsentQuery, datasetFacts: false);

        Roots(doc).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQueryGraphql_WithoutFlag_HasNoAutherisRoots()
    {
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = ConsentQuery });

        isError.ShouldBeFalse(payload.ToString());
        payload.TryGetProperty("extensions", out var ext).ShouldBeTrue();
        ext.TryGetProperty("autheris", out var autheris).ShouldBeFalse(autheris.ToString());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VirtualFilterOnly_DoesNotSetRowFilterApplied_AndAgreesWithDescribeMyAccess()
    {
        // air1 is readable without a consent row filter; only the Virtual Filter of the fixture narrows it.
        using var doc = await GraphQlRawAsync("{ a: default_main_air1(first: 20) { id } }", datasetFacts: true);

        var a = Roots(doc)!.Value.GetProperty("a");
        a.GetProperty("rowFilterApplied").GetBoolean().ShouldBeFalse();
        Descriptions(a).ShouldBeEmpty();
        (await ActiveRowFiltersAsync(_fixture.Client(), "default.main.air1")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyToken_SeesConstantDescriptionsOnly()
    {
        using var doc = await GraphQlRawAsync(Caller(ReadOnlyScope), ConsentQuery, datasetFacts: true);

        var a = Roots(doc)!.Value.GetProperty("a");
        a.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        Descriptions(a).ShouldAllBe(d => Constants.Contains(d));
        var text = a.GetRawText();
        text.ShouldNotContain("region");
        text.ShouldNotContain("EU");
        (await ActiveRowFiltersAsync(Caller(ReadOnlyScope), "default.main.facts_consent")).ShouldBe(Descriptions(a));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenRelationTarget_NotListed_RealDb()
    {
        // Control: the readable parent alone produces an entry without any relation.
        using var control = await GraphQlRawAsync("{ ok: default_main_facts_plain(first: 5) { id } }", datasetFacts: true);
        Roots(control)!.Value.GetProperty("ok").GetProperty("relations").GetArrayLength().ShouldBe(0);

        // The relation target is not readable for the caller: the field is not exposed, and nothing about the table shows up in the facts.
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ bad: default_main_facts_plain(first: 5) { id notes { id } } }",
            extensions = new { autheris = new { datasetFacts = true } }
        });
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);

        text.ShouldNotContain("facts_hidden");
        if (Roots(doc) is { } roots && roots.TryGetProperty("bad", out var bad))
        {
            bad.GetProperty("relations").EnumerateArray().Select(r => r.GetProperty("dataset").GetString())
                .ShouldNotContain("default.main.facts_hidden");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OwnerToken_DescriptionsEqualNonOwner_NoSql()
    {
        using var nonOwner = await GraphQlRawAsync(Caller(), ConsentQuery, datasetFacts: true);
        using var admin = await GraphQlRawAsync(Caller(roles: "Reader,GovernanceAdmin"), ConsentQuery, datasetFacts: true);

        // Positive control: describe_my_access shows the admin the definition, so the SQL is available to leak.
        var adminFilters = await ActiveRowFiltersAsync(Caller(roles: "Reader,GovernanceAdmin"), "default.main.facts_consent");
        adminFilters.ShouldContain(f => f.Contains("region", StringComparison.OrdinalIgnoreCase));

        var adminFacts = Roots(admin)!.Value.GetProperty("a");
        Descriptions(adminFacts).ShouldBe(Descriptions(Roots(nonOwner)!.Value.GetProperty("a")));
        adminFacts.GetRawText().ShouldBe(Roots(nonOwner)!.Value.GetProperty("a").GetRawText());
        adminFacts.GetRawText().ShouldNotContain("region");
        adminFacts.GetRawText().ShouldNotContain("EU");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentPerCellFilter_FlagMatchesDescribeMyAccess()
    {
        using var doc = await GraphQlRawAsync("{ c: default_main_facts_cell(first: 20) { id region } }", datasetFacts: true);

        var c = Roots(doc)!.Value.GetProperty("c");
        var described = await ActiveRowFiltersAsync(_fixture.Client(), "default.main.facts_cell");
        c.GetProperty("rowFilterApplied").GetBoolean().ShouldBe(described.Length > 0);
        // A per-cell consent restricts rows per signature: it must never be reported as "no filter".
        c.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        Descriptions(c).ShouldBe(described);
        c.GetRawText().ShouldNotContain("EU");
        c.GetRawText().ShouldNotContain("secret");
    }

    private sealed class ReadOnlyTestClaimsTransformation(IIdentityPipeline pipeline, IHttpContextAccessor? httpContextAccessor = null) : IClaimsTransformation
    {
        private readonly EnterpriseClaimsTransformation _inner = new(pipeline, httpContextAccessor);

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var transformed = await _inner.TransformAsync(principal).ConfigureAwait(false);
            if (transformed.HasScope(ReadOnlyScope) && transformed.Identity is ClaimsIdentity identity)
            {
                TokenAccessScope.MarkReadOnly(identity);
            }

            return transformed;
        }
    }

    public sealed class FactsFixture : RowFilterChannelParityTests.Fixture
    {
        private static readonly TableIdentifier ConsentTable = new("default", "main", "facts_consent");
        private static readonly TableIdentifier CellTable = new("default", "main", "facts_cell");
        private static readonly TableIdentifier PlainTable = new("default", "main", "facts_plain");
        private static readonly TableIdentifier HiddenTable = new("default", "main", "facts_hidden");

        protected override IReadOnlyCollection<string> RequiredGraphQlFields =>
            [.. base.RequiredGraphQlFields, "default_main_facts_consent", "default_main_facts_cell", "default_main_facts_plain"];

        protected override void ConfigureHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services => services.AddTransient<IClaimsTransformation, ReadOnlyTestClaimsTransformation>());

        protected override void SeedExtraData(NpgsqlConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE facts_consent (id INTEGER PRIMARY KEY, region TEXT);
                INSERT INTO facts_consent VALUES (1, 'EU'), (2, 'US'), (3, 'EU');
                CREATE TABLE facts_cell (id INTEGER PRIMARY KEY, region TEXT, secret TEXT);
                INSERT INTO facts_cell VALUES (1, 'EU', 's1'), (2, 'US', 's2'), (3, 'EU', 's3');
                CREATE TABLE facts_plain (id INTEGER PRIMARY KEY, label TEXT);
                INSERT INTO facts_plain VALUES (1, 'a'), (2, 'b');
                CREATE TABLE facts_hidden (id INTEGER PRIMARY KEY, plain_id INTEGER, note TEXT);
                INSERT INTO facts_hidden VALUES (1, 1, 'n1');
                """;
            cmd.ExecuteNonQuery();
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var consent = await RegisterWithKeyAsync(repo, ConsentTable, ("id", "integer"), ("region", "varchar"));
            var cell = await RegisterWithKeyAsync(repo, CellTable, ("id", "integer"), ("region", "varchar"), ("secret", "varchar"));
            var plain = await RegisterWithKeyAsync(repo, PlainTable, ("id", "integer"), ("label", "varchar"));
            var hidden = await RegisterWithKeyAsync(repo, HiddenTable, ("id", "integer"), ("plain_id", "integer"), ("note", "varchar"));

            await repo.CreateConsentAsync(RegionConsent(consent, ConsentTable, "EU", "id", "region"));
            await repo.CreateConsentAsync(Consent(plain, PlainTable, []));

            // Two overlapping grants whose column rules differ: the decision becomes a per-cell policy without combined SQL.
            await repo.CreateConsentAsync(RegionConsent(cell, CellTable, "EU", "id", "region", "secret"));
            await repo.CreateConsentAsync(RegionConsent(cell, CellTable, "US", "id", "region"));

            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITableRelationRepository>().CreateRelationAsync(new TableRelation
            {
                ParentTableId = plain.Table.Id,
                ParentTableIdentifier = PlainTable,
                ChildTableId = hidden.Table.Id,
                ChildTableIdentifier = HiddenTable,
                RelationName = "notes",
                JoinKeysParent = ["id"],
                JoinKeysChild = ["plain_id"],
                Cardinality = RelationCardinality.OneToMany
            });
        }

        private static async Task<TableMetadata> RegisterWithKeyAsync(IGovernanceRepository repo, TableIdentifier id, params (string Name, string Type)[] columns)
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = "default" },
                Identifier = id,
                PrimaryKeyColumns = ["id"],
                ReplaceKeyDefinition = true,
                Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList()
            });
            return (await repo.GetTableMetadataAsync(id))!;
        }

        private static Consent RegionConsent(TableMetadata meta, TableIdentifier id, string region, params string[] clearColumns) => new()
        {
            TableId = meta.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(DavidSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = meta.Columns
                .Select(c => new ConsentColumnRule
                {
                    ColumnName = c.ColumnName,
                    AccessLevel = clearColumns.Contains(c.ColumnName) ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny
                })
                .ToList(),
            RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = JsonSerializer.Serialize(region), ValueSource = "LITERAL" }]
        };
    }
}
