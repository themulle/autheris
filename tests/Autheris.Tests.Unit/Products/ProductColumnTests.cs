using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductColumnTests
{
    private static readonly Dictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> Catalog = new()
    {
        [ProductHarness.Customers] = ProductHarness.Columns(ProductHarness.Customers)!,
        [ProductHarness.Orders] = ProductHarness.Columns(ProductHarness.Orders)!
    };

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = FastSqlEngine.Default.Parse(sql.AsMemory());
        return new SqlAstBuilder(new AstBuilderOptions
        {
            AllowedFunctions = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.DuckDb)
        }).BuildStatement(tree);
    }

    private static IReadOnlyList<DataProductColumn> Classify(string sql, IReadOnlyList<DataProductColumn>? previous = null)
    {
        var outputs = ProductLineageExtractor.Extract(Parse(sql), Catalog).ShouldNotBeNull();
        return ProductColumnClassifier.Classify(outputs, l => Catalog[l.Table].Single(c => c.Name == l.Column).IsPii, previous);
    }

    [Fact]
    public void Pii_lineage_defaults_to_masked()
    {
        var columns = Classify("SELECT c.email, upper(c.email) AS shouting, c.segment, o.total FROM crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id");

        columns.Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiMasked);
        columns.Single(c => c.Name == "shouting").Class.ShouldBe(ProductColumnClass.PiiMasked);
        columns.Single(c => c.Name == "segment").Class.ShouldBe(ProductColumnClass.NonPii);
        columns.Single(c => c.Name == "total").Class.ShouldBe(ProductColumnClass.NonPii);
        columns.Single(c => c.Name == "email").Lineage.ShouldBe([new ColumnLineage(ProductHarness.Customers, "email")]);
        columns.Single(c => c.Name == "total").DataType.ShouldBe("decimal");
    }

    [Fact]
    public void Pii_lineage_is_followed_through_wildcards_aliases_subqueries_and_ctes()
    {
        Classify("SELECT * FROM crm.public.customers").Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiMasked);
        Classify("SELECT x.mail FROM (SELECT email AS mail FROM crm.public.customers) x").Single().Class.ShouldBe(ProductColumnClass.PiiMasked);
        Classify("WITH c AS (SELECT email, id FROM crm.public.customers) SELECT id, email FROM c").Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiMasked);
        Classify("SELECT id FROM crm.public.customers UNION ALL SELECT customer_id FROM crm.public.orders").Single().Class.ShouldBe(ProductColumnClass.NonPii);
        Classify("SELECT segment FROM crm.public.customers UNION ALL SELECT email FROM crm.public.customers").Single().Class.ShouldBe(ProductColumnClass.PiiMasked);
        Classify("SELECT count(*) AS n, 1 AS one FROM crm.public.customers").ShouldAllBe(c => c.Class == ProductColumnClass.NonPii);
    }

    [Fact]
    public void Unresolved_lineage_is_pii()
    {
        // the unqualified "id" exists in both sources, the subquery is a nested scope, and "missing" is in no source
        var columns = Classify("SELECT id, (SELECT max(total) FROM crm.public.orders) AS biggest, missing FROM crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id");

        columns.ShouldAllBe(c => c.Class == ProductColumnClass.PiiMasked);
        ProductColumnClassifier.Classify([new ProductOutputColumn("x", [], true)], _ => false).Single().Class.ShouldBe(ProductColumnClass.PiiMasked);
    }

    [Fact]
    public void Only_catalog_ids_are_resolved_and_duplicates_are_rejected()
    {
        ProductLineageExtractor.CollectBaseObjects(Parse("SELECT id FROM orders")).ShouldBeNull();
        ProductLineageExtractor.CollectBaseObjects(Parse("SELECT id FROM crm.public.orders")).ShouldBe([ProductHarness.Orders]);
        ProductLineageExtractor.Extract(Parse("SELECT id, id FROM crm.public.orders"), Catalog).ShouldBeNull();
        ProductLineageExtractor.Extract(Parse("SELECT id FROM crm.public.orders"), new Dictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>>()).ShouldBeNull();
    }

    [Fact]
    public void Declassification_requires_reason_and_is_audited()
    {
        var masked = Classify("SELECT email FROM crm.public.customers").Single();

        ProductColumnClassifier.Declassify(masked, ProductColumnClass.NonPii, null, ProductHarness.Alice).ErrorCode.ShouldBe(ProductErrorCodes.ReasonRequired);
        ProductColumnClassifier.Declassify(masked, ProductColumnClass.PiiClearEligible, "  ", ProductHarness.Alice).ErrorCode.ShouldBe(ProductErrorCodes.ReasonRequired);
        var done = ProductColumnClassifier.Declassify(masked, ProductColumnClass.PiiClearEligible, "hashed upstream", ProductHarness.Alice).Column!;
        done.Class.ShouldBe(ProductColumnClass.PiiClearEligible);
        (done.DeclassifiedBy, done.DeclassificationReason).ShouldBe((ProductHarness.Alice.Value, "hashed upstream"));
        ProductColumnClassifier.Declassify(done, ProductColumnClass.PiiMasked, null, ProductHarness.Alice).Column!.DeclassifiedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Declassification_by_the_creator_is_audited_and_survives_only_an_unchanged_definition()
    {
        var h = new ProductHarness();
        h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Customers];
        var alice = h.Caller(ProductHarness.Alice);
        const string sql = "SELECT id, email FROM crm.public.customers";
        var created = await h.Service.PutAsync(h.Put(alice, sql));
        created.Columns.Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiMasked);

        var missingReason = await Should.ThrowAsync<ProductException>(() =>
            h.Service.ClassifyColumnAsync(alice, "sales", "orders_view", "email", ProductColumnClass.NonPii, null));
        missingReason.Code.ShouldBe(ProductErrorCodes.ReasonRequired);

        var updated = await h.Service.ClassifyColumnAsync(alice, "sales", "orders_view", "email", ProductColumnClass.PiiClearEligible, "pseudonymised");
        updated.Columns.Single(c => c.Name == "email").DeclassifiedBy.ShouldBe(ProductHarness.Alice.Value);
        var entry = h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductColumnClassificationChanged);
        entry.ActorSid.ShouldBe(ProductHarness.Alice);
        entry.DetailsJson.ShouldContain("pseudonymised");

        // the same definition keeps the declassification; any changed definition falls back to the lineage default
        var same = await h.Service.PutAsync(h.Put(alice, sql));
        same.Columns.Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiClearEligible);
        var other = await h.Service.PutAsync(h.Put(alice, "SELECT id, lower(email) AS email FROM crm.public.customers"));
        other.Columns.Single(c => c.Name == "email").Class.ShouldBe(ProductColumnClass.PiiMasked);

        var unknown = await Should.ThrowAsync<ProductException>(() =>
            h.Service.ClassifyColumnAsync(alice, "sales", "orders_view", "nope", ProductColumnClass.NonPii, "x"));
        unknown.Failure.ShouldBe(ProductFailure.NotFound);
    }
}
