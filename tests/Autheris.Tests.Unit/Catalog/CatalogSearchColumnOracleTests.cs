namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// WP-0 (PLAN-GOV-111 A-0.2, W0-C1): the search index is built from the whole catalog including column names, so search must not be a
/// column-name oracle. T-W0-1 (search part), T-W0-2, T-W0-3, M-5 baseline.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogSearchColumnOracleTests
{
    private const string Sid = "S-A-USER";

    private static readonly TableMetadata Customers = Wp0.Table("crm", "customers", "Customer master data", ["id", "name", "ssn_hash"]);
    private static readonly TableMetadata Orders = WithForeignKey(
        Wp0.Table("crm", "orders", "Order headers", ["order_id", "buyer_ref", "secret_ref", "customer_id"]),
        ("buyer_ref", "customers", "id"),
        ("secret_ref", "customers", "ssn_hash"));

    private static TableMetadata WithForeignKey(TableMetadata table, params (string Column, string Target, string TargetColumn)[] keys)
    {
        var columns = table.Columns.Select(c =>
        {
            var key = keys.FirstOrDefault(k => k.Column == c.ColumnName);
            return key.Column == null
                ? c
                : new TableColumn { ColumnName = c.ColumnName, DataType = c.DataType, Meta = new Dictionary<string, string> { ["foreign_key_table"] = key.Target, ["foreign_key_column"] = key.TargetColumn } };
        }).ToList();
        return table with { Columns = columns };
    }

    private static async Task<(Autheris.Application.Catalog.Services.CatalogDiscoveryService Service, RequestContext Context)> ArrangeAsync(
        TenancyMode mode, string sid, params Consent[] consents)
    {
        var tables = new[] { Customers, Orders };
        var engine = new CatalogSearchEngine(new LocalDeterministicEmbeddingGenerator(), Microsoft.Extensions.Options.Options.Create(new CatalogSearchOptions()), NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync(tables);
        var service = Wp0.Service(Wp0.Tables(tables), Wp0.Consents(consents), Wp0.Options(mode), engine);
        var user = Wp0.User(sid, mode == TenancyMode.Single ? null : "tenant-a", mode == TenancyMode.Single ? ["GovernanceAdmin"] : null);
        return (service, Wp0.Context(user));
    }

    private static Consent[] RestrictedConsents() =>
    [
        // ssn_hash and secret_ref are not granted: they must stay invisible in every search output.
        Wp0.Allow(Customers.Identifier, Sid, Wp0.TenantA, "id", "name"),
        Wp0.Allow(Orders.Identifier, Sid, Wp0.TenantA, "order_id", "buyer_ref", "customer_id")
    ];

    private static string Everything(IEnumerable<CatalogSearchHit> hits) => System.Text.Json.JsonSerializer.Serialize(hits);

    // T-W0-1 (search part) and T-W0-2: a hidden column name leaves no trace in any field of a hit that survives on a visible term.
    [Theory]
    [InlineData(CatalogSearchMode.Hybrid)]
    [InlineData(CatalogSearchMode.KeywordBm25)]
    public async Task Search_QueryWithVisibleAndHiddenTerm_LeaksNoTraceOfTheHiddenColumn(CatalogSearchMode mode)
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Multi, Sid, RestrictedConsents());

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("master ssn_hash", Mode: mode, Limit: 10), context);

        hits.Select(h => h.TableIdentifier.TableName).ShouldContain("customers");
        Everything(hits).ShouldNotContain("ssn_hash");
        Everything(hits).ShouldNotContain("secret_ref");
        hits.First(h => h.TableIdentifier.TableName == "customers").MatchedTerms.ShouldContain("master");
    }

    // T-W0-3: a hit that exists only because of a hidden column is dropped (BM25 and vector).
    [Theory]
    [InlineData(CatalogSearchMode.Hybrid)]
    [InlineData(CatalogSearchMode.KeywordBm25)]
    [InlineData(CatalogSearchMode.SemanticVector)]
    public async Task Search_QueryMatchingOnlyAHiddenColumn_DropsTheHit(CatalogSearchMode mode)
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Multi, Sid, RestrictedConsents());

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("ssn_hash", Mode: mode, Limit: 10), context);

        hits.ShouldBeEmpty();
        (await service.SearchCatalogAsync("ssn_hash", null, context)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_SameHiddenColumnQuery_IsStillFoundForAnUnrestrictedAdministrator()
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Single, "S-ADMIN");

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("ssn_hash", Mode: CatalogSearchMode.KeywordBm25, Limit: 10), context);

        hits.Select(h => h.TableIdentifier.TableName).ShouldContain("customers");
        hits.First().RelevantColumns.ShouldContain("ssn_hash");
    }

    [Fact]
    public async Task Search_RelevantColumns_ComeOnlyFromTheCallersVisibleColumns()
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Multi, Sid, RestrictedConsents());

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("customers name", Mode: CatalogSearchMode.KeywordBm25, Limit: 10), context);

        var customers = hits.First(h => h.TableIdentifier.TableName == "customers");
        customers.RelevantColumns.ShouldBe(["name"]);
    }

    // T-W0-3 (join paths): a path over a hidden FromColumn or ToColumn is removed; a path over visible columns stays.
    [Fact]
    public async Task Search_RelatedJoinPaths_AreKeptOnlyOverVisibleColumns()
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Multi, Sid, RestrictedConsents());

        var hits = await service.SearchCatalogDetailedAsync(
            new CatalogSearchQuery("order headers", Mode: CatalogSearchMode.KeywordBm25, Limit: 10, ExpandRelations: true), context);

        var orders = hits.First(h => h.TableIdentifier.TableName == "orders");
        // customer_id is visible but its convention-derived target column (customers.customer_id) is not a visible column: dropped as well.
        orders.RelatedJoinPaths.Select(r => r.FromColumn).ShouldBe(["buyer_ref"]);
        orders.RelatedJoinPaths.Select(r => r.ToColumn).ShouldBe(["id"]);
        Everything(hits).ShouldNotContain("ssn_hash");
        Everything(hits).ShouldNotContain("secret_ref");
    }

    [Fact]
    public async Task Search_HitForATableWithoutConsent_NeverAppears_AndIndexDescriptionComesFromTheFilteredRow()
    {
        var (service, context) = await ArrangeAsync(TenancyMode.Multi, Sid, Wp0.Allow(Customers.Identifier, Sid, Wp0.TenantA));

        var hits = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("order headers customer", Mode: CatalogSearchMode.KeywordBm25, Limit: 10), context);

        hits.Select(h => h.TableIdentifier.TableName).ShouldBe(["customers"]);
    }
}
