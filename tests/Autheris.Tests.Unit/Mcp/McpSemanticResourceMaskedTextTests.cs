namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// The MCP semantic resources (glossary, column docs) follow the catalog text rule of the discovery path: the description, long
/// description and meta text of a column that is masked for the caller never reach the client, and an undecidable case withholds all text.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpSemanticResourceMaskedTextTests
{
    private const string Tenant = "tenant-a";
    private const string Canary = "CANARY-NOT-FOR-AGENTS";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier OrdersArchive = new("sales", "archive", "orders");

    private static TableMetadata Table(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceName = id.Domain, SourceType = "PostgreSQL" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int", Description = "Order key" },
            new TableColumn
            {
                ColumnName = "secret",
                DataType = "varchar",
                Description = Canary + " description",
                LongDescription = Canary + " long text",
                Meta = new Dictionary<string, string> { ["description"] = Canary + " meta" }
            }
        ]
    };

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MCP-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    private static TableAccessDecision Decision(TableIdentifier id, bool hideText, RestrictedScopeClearance? clearance = null) =>
        new(id, true, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["secret"] = ColumnAccessLevel.Mask
        }, null, [], false)
        {
            HiddenCatalogTextColumns = hideText ? new HashSet<string>(["secret"], StringComparer.OrdinalIgnoreCase) : null,
            ScopeClearance = clearance
        };

    /// <summary>A decision point answering <see cref="AccessOperation.Discover"/> batches from a function (no mocking framework).</summary>
    private sealed class FakeDecisionPoint(Func<IReadOnlyList<TableMetadata>, IReadOnlyList<TableAccessDecision>> decide) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            ValueTask.FromResult(decide(objects));

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static SemanticMcpCompiler Compiler(
        Func<IReadOnlyList<TableMetadata>, IReadOnlyList<TableAccessDecision>> decide,
        bool hideForRestrictedScopes = false,
        params TableMetadata[] tables) =>
        McpSemanticResourceSecurityTests.Compiler(
            tables,
            visibility: null,
            decisionPoint: new FakeDecisionPoint(decide),
            discoveryOptions: Options.Create(new CatalogDiscoveryOptions
            {
                Discovery = new CatalogDiscoverySettings { HideMaskedColumnCatalogTextForRestrictedScopes = hideForRestrictedScopes }
            }));

    private static IReadOnlyList<TableAccessDecision> HideSecret(IReadOnlyList<TableMetadata> t) => t.Select(x => Decision(x.Identifier, hideText: true)).ToList();

    [Fact]
    public async Task Glossary_MaskedColumnWithHiddenText_OmitsTheDescription()
    {
        var compiler = Compiler(HideSecret, tables: Table(Orders));

        var glossary = await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/public/orders");

        glossary.ShouldNotBeNull();
        glossary.Text.ShouldNotContain(Canary);
        glossary.Text.ShouldContain("Order key");
        glossary.Text.ShouldContain("`secret`");
    }

    [Fact]
    public async Task ColumnDocs_MaskedColumnWithHiddenText_AnswersNotFoundAndIsNotListed()
    {
        var compiler = Compiler(HideSecret, tables: Table(Orders));

        (await compiler.ReadSemanticResourceAsync(User(), "dbt://models/sales/public/orders/columns/secret/docs")).ShouldBeNull();
        (await compiler.ReadSemanticResourceAsync(User(), "dbt://models/orders/columns/secret/docs")).ShouldBeNull();
        (await compiler.ReadSemanticResourceAsync(User(), "dbt://models/sales/public/orders/columns/id/docs")).ShouldNotBeNull();

        var page = await compiler.ListSemanticResourceUrisAsync(User());
        page.Items.ShouldNotContain(r => r.Uri.Contains("secret"));
        page.Items.ShouldContain(r => r.Uri == "dbt://models/sales/public/orders/columns/id/docs");
    }

    [Fact]
    public async Task GetSemanticResources_MaskedColumnWithHiddenText_AppearsInNoResourceText()
    {
        var compiler = Compiler(HideSecret, tables: Table(Orders));

        var resources = await compiler.GetSemanticResourcesAsync(principal: User());

        resources.ShouldNotBeEmpty();
        resources.ShouldAllBe(r => !r.Text.Contains(Canary) && !r.Uri.Contains("secret"));
    }

    [Fact]
    public async Task RestrictedScope_MaskedColumn_HidesTextWhenTheTenantOptionIsOn()
    {
        var clearance = new RestrictedScopeClearance(
            new HashSet<string>(["Agent.Read"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiler = Compiler(t => t.Select(x => Decision(x.Identifier, hideText: false, clearance)).ToList(), hideForRestrictedScopes: true, Table(Orders));

        var glossary = await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/public/orders");

        glossary.ShouldNotBeNull();
        glossary.Text.ShouldNotContain(Canary);
        (await compiler.ReadSemanticResourceAsync(User(), "dbt://models/sales/public/orders/columns/secret/docs")).ShouldBeNull();
    }

    [Fact]
    public async Task ClearColumn_KeepsItsDescription()
    {
        var compiler = Compiler(t => t.Select(x => Decision(x.Identifier, hideText: false)).ToList(), tables: Table(Orders));

        var glossary = await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/public/orders");

        glossary.ShouldNotBeNull();
        glossary.Text.ShouldContain("Order key");
        glossary.Text.ShouldContain(Canary + " description");
    }

    [Fact]
    public async Task UndecidableAccess_WithholdsAllColumnText()
    {
        var compiler = Compiler(_ => throw new InvalidOperationException("pdp down"), tables: Table(Orders));

        var glossary = await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/public/orders");
        var resources = await compiler.GetSemanticResourcesAsync(principal: User());

        glossary.ShouldNotBeNull();
        glossary.Text.ShouldNotContain(Canary);
        glossary.Text.ShouldNotContain("Order key");
        resources.ShouldAllBe(r => !r.Text.Contains(Canary) && !r.Text.Contains("Order key"));
        (await compiler.ReadSemanticResourceAsync(User(), "dbt://models/sales/public/orders/columns/id/docs")).ShouldBeNull();
    }

    [Fact]
    public async Task Uris_CarryTheSchema_AndSameNamedTablesStayApart()
    {
        var compiler = Compiler(t => t.Select(x => Decision(x.Identifier, hideText: false)).ToList(), tables: [Table(Orders), Table(OrdersArchive)]);

        var page = await compiler.ListSemanticResourceUrisAsync(User());
        page.Items.ShouldContain(r => r.Uri == "glossary://sales/public/orders");
        page.Items.ShouldContain(r => r.Uri == "glossary://sales/archive/orders");

        var archive = await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/archive/orders");
        archive.ShouldNotBeNull();
        archive.Text.ShouldContain("sales.orders");
        archive.Uri.ShouldBe("glossary://sales/archive/orders");

        // The form without the schema is ambiguous for these two tables and answers like an unknown one.
        (await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/orders")).ShouldBeNull();
    }
}
