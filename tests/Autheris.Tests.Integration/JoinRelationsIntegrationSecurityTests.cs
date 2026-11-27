namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// The <c>join_relations</c> of <c>search_catalog</c> come from the stored table relations and are filtered by the caller's own view:
/// a table of another tenant and a column the caller is denied never show up, over the real pipeline.
/// </summary>
[Trait("Category", "Security")]
public sealed class JoinRelationsIntegrationSecurityTests : IClassFixture<JoinRelationsIntegrationSecurityTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string SharedSource = "shared_src";
    private static readonly TableIdentifier OrdersA = new("dom_a", "main", "orders_a");
    private static readonly TableIdentifier CustomersA = new("dom_a", "main", "customers_a");
    private static readonly TableIdentifier InvoicesB = new("dom_b", "main", "invoices_b");

    public sealed class HostFixture : IDisposable
    {
        private readonly SemaphoreSlim _seedLock = new(1, 1);
        private bool _seeded;

        public WebApplicationFactory<Program> Root { get; } = new();

        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("JoinRelationsSecurity"));
                b.UseSetting("Gateway:Mcp:Enabled", "true");
                b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            });
        }

        public async Task EnsureSeededAsync()
        {
            await _seedLock.WaitAsync();
            try
            {
                if (_seeded)
                {
                    return;
                }

                await SeedAsync(Factory);
                _seeded = true;
            }
            finally
            {
                _seedLock.Release();
            }
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
            _seedLock.Dispose();
        }
    }

    private readonly HostFixture _fixture;

    public JoinRelationsIntegrationSecurityTests(HostFixture fixture) => _fixture = fixture;

    private static async Task<TableMetadata> RegisterAsync(IGovernanceRepository repo, TableIdentifier id, string description, params string[] columns) =>
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = SharedSource, Description = description },
            Identifier = id,
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "integer" }).ToList()
        });

    private static Task ConsentAsync(IGovernanceRepository repo, TableMetadata table, string sid, string tenant, params ConsentColumnRule[] rules) =>
        repo.CreateConsentAsync(new Consent
        {
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            TenantId = new TenantId(tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(sid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = rules.ToList()
        });

    private static ConsentColumnRule Clear(string column) => new() { ColumnName = column, AccessLevel = ColumnAccessLevel.Clear };

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var orders = await RegisterAsync(repo, OrdersA, "Orders of tenant A", "id", "customer_id", "invoice_ref");
        var customers = await RegisterAsync(repo, CustomersA, "Customers of tenant A", "id", "segment");
        var invoices = await RegisterAsync(repo, InvoicesB, "Invoices of tenant B", "id", "order_ref");

        await ConsentAsync(repo, orders, "S-A-USER", TenantA, Clear("id"), Clear("customer_id"), Clear("invoice_ref"));
        await ConsentAsync(repo, customers, "S-A-USER", TenantA, Clear("id"), Clear("segment"));
        await ConsentAsync(repo, invoices, "S-B-USER", TenantB, Clear("id"), Clear("order_ref"));

        // A second tenant-A caller for whom the join column of the customers table is denied under the Agent.Read scope.
        await ConsentAsync(repo, orders, "S-A-SCOPED", TenantA, Clear("id"), Clear("customer_id"), Clear("invoice_ref"));
        await ConsentAsync(
            repo,
            customers,
            "S-A-SCOPED",
            TenantA,
            Clear("id"),
            Clear("segment"),
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Deny, ApplicableScopes = ["Agent.Read"] });

        var relations = scope.ServiceProvider.GetRequiredService<ITableRelationRepository>();
        await relations.CreateRelationAsync(new TableRelation
        {
            ParentTableId = customers.Table.Id,
            ParentTableIdentifier = CustomersA,
            ChildTableId = orders.Table.Id,
            ChildTableIdentifier = OrdersA,
            RelationName = "customer_orders",
            JoinKeysParent = ["id"],
            JoinKeysChild = ["customer_id"],
            Cardinality = RelationCardinality.OneToMany
        });
        await relations.CreateRelationAsync(new TableRelation
        {
            ParentTableId = invoices.Table.Id,
            ParentTableIdentifier = InvoicesB,
            ChildTableId = orders.Table.Id,
            ChildTableIdentifier = OrdersA,
            RelationName = "invoice_orders",
            JoinKeysParent = ["id"],
            JoinKeysChild = ["invoice_ref"],
            Cardinality = RelationCardinality.OneToMany
        });
    }

    private async Task<HttpClient> ClientAsync(string sid, string tenant, string? scope = null)
    {
        await _fixture.EnsureSeededAsync();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        if (scope != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }

        return client;
    }

    private static async Task<(string Raw, List<JsonElement> Hits)> SearchAsync(HttpClient client, string query)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (isError, text) = await McpTestClient.CallAsync(mcp, "search_catalog", new { query });
        isError.ShouldBeFalse(text);
        using var doc = JsonDocument.Parse(text);
        return (text, doc.RootElement.GetProperty("datasets").EnumerateArray().Select(static d => d.Clone()).ToList());
    }

    private static IEnumerable<string> Targets(JsonElement hit) =>
        hit.GetProperty("join_relations").EnumerateArray().Select(static r => r.GetProperty("target_table").GetString()!);

    [Fact]
    public async Task SearchCatalog_StoredRelation_ListedWithDirection_ForTheOwningTenant()
    {
        var (_, hits) = await SearchAsync(await ClientAsync("S-A-USER", TenantA), "orders_a");

        var relation = hits.Single(h => h.GetProperty("table").GetString() == "dom_a.main.orders_a").GetProperty("join_relations").EnumerateArray()
            .Single(r => r.GetProperty("target_table").GetString() == "dom_a.main.customers_a");
        relation.GetProperty("direction").GetString().ShouldBeOneOf("outgoing", "incoming");
        relation.GetProperty("join_condition").GetString().ShouldBe("orders_a.customer_id = customers_a.id");
    }

    [Fact]
    public async Task SearchCatalog_TenantB_CannotSeeTenantAStoredRelationTarget()
    {
        var (rawA, hitsA) = await SearchAsync(await ClientAsync("S-A-USER", TenantA), "orders_a");
        var (rawB, hitsB) = await SearchAsync(await ClientAsync("S-B-USER", TenantB), "invoices");

        // Tenant A never learns of the tenant-B table that a stored relation points to.
        rawA.ShouldNotContain("invoices_b");
        hitsA.SelectMany(Targets).ShouldNotContain("dom_b.main.invoices_b");

        // Tenant B sees its own table, but nothing of the tenant-A table on the other end of the stored relation.
        hitsB.Select(static h => h.GetProperty("table").GetString()).ShouldContain("dom_b.main.invoices_b");
        rawB.ShouldNotContain("orders_a");
        rawB.ShouldNotContain("invoice_ref");
    }

    [Fact]
    public async Task SearchCatalog_ColumnDenyConsent_JoinColumnAbsent()
    {
        // Reference: without the scope the join column is visible, so its absence below is caused by the Deny rule.
        var (_, plain) = await SearchAsync(await ClientAsync("S-A-SCOPED", TenantA), "orders_a");
        plain.SelectMany(Targets).ShouldContain("dom_a.main.customers_a");

        var (raw, scoped) = await SearchAsync(await ClientAsync("S-A-SCOPED", TenantA, "Agent.Read"), "orders_a");

        scoped.SelectMany(Targets).ShouldNotContain("dom_a.main.customers_a");
        scoped.SelectMany(static h => h.GetProperty("join_relations").EnumerateArray())
            .Select(static r => r.GetProperty("join_condition").GetString()!)
            .ShouldAllBe(static c => !c.Contains("customers_a.id", StringComparison.Ordinal));
        raw.ShouldNotContain("customers_a.id");
    }
}
