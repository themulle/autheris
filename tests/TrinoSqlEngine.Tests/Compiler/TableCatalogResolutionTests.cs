using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-17: the Unity Catalog part takes part in the catalog key; two catalogs with one schema.table are never confused.</summary>
public class TableCatalogResolutionTests
{
    private static TableCatalogEntry Entry(string? catalog, string schema, string table, long version = 1) => new(
        new TableIdentity(schema, table, catalog), ImmutableArray.Create(new CatalogColumn("id", "INT")), null, version);

    private static SqlQualifiedName Name(params string[] parts) => new(parts.Select(p => new SqlIdentifier(p)).ToList());

    private static InMemoryTableCatalog TwoCatalogs() => new(new[] { Entry("main", "s", "t", 1), Entry("dev", "s", "t", 2) }, "s");

    [Fact]
    public void ThreePartName_SelectsTheEntryOfThatCatalog()
    {
        var catalog = TwoCatalogs();
        Assert.Equal(1, catalog.Resolve(Name("main", "s", "t"))!.Version);
        Assert.Equal(2, catalog.Resolve(Name("dev", "s", "t"))!.Version);
        Assert.Equal(2, catalog.Resolve(Name("DEV", "S", "T"))!.Version);
    }

    [Fact]
    public void ThreePartName_OfAnUnknownCatalog_DoesNotResolve()
    {
        Assert.Null(TwoCatalogs().Resolve(Name("prod", "s", "t")));
    }

    [Fact]
    public void OneOrTwoPartName_IsAmbiguousAcrossCatalogs()
    {
        var catalog = TwoCatalogs();
        Assert.Null(catalog.Resolve(Name("s", "t")));
        Assert.Null(catalog.Resolve(Name("t")));
    }

    [Fact]
    public void OneOrTwoPartName_ResolvesWhenOnlyOneCatalogHasTheTable()
    {
        var catalog = new InMemoryTableCatalog(new[] { Entry("main", "s", "t"), Entry("dev", "s", "other") }, "s");
        Assert.NotNull(catalog.Resolve(Name("s", "t")));
        Assert.NotNull(catalog.Resolve(Name("t")));
    }

    [Fact]
    public void EntriesWithoutACatalogPart_IgnoreTheFirstPartOfAThreePartName()
    {
        var catalog = new InMemoryTableCatalog(new[] { Entry(null, "dbo", "orders") }, "dbo");
        Assert.NotNull(catalog.Resolve(Name("trinocat", "dbo", "orders")));
        Assert.NotNull(catalog.Resolve(Name("dbo", "orders")));
    }

    [Fact]
    public void SpellingsThatDifferOnlyByCase_RemainAmbiguous()
    {
        var catalog = new InMemoryTableCatalog(new[] { Entry("main", "s", "T"), Entry("main", "s", "t") }, "s");
        Assert.Null(catalog.Resolve(Name("main", "s", "t")));
        Assert.Null(catalog.Resolve(Name("s", "t")));
    }

    [Fact]
    public void Compile_AgainstTwoCatalogs_SecuresTheRequestedOne()
    {
        var engine = new FastSqlEngine();
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(new TableIdentity("s", "t", "main"), ImmutableArray.Create(new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING")), "tenantid", 1),
            new TableCatalogEntry(new TableIdentity("s", "t", "dev"), ImmutableArray.Create(new CatalogColumn("id", "INT"), new CatalogColumn("tenantid", "STRING")), "tenantid", 2)
        }, "s");
        CompileRequest Request() => new()
        {
            TargetDialect = TargetSqlDialect.Databricks,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowExperimentalDialect = true,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", TrinoSqlEngine.Ast.Emit.SqlParameterType.String)
            }
        };

        var dev = engine.Compile("SELECT id FROM dev.s.t".AsMemory(), Request(), CancellationToken.None);
        Assert.Contains("`dev`.`s`.`t`", dev.Sql);
        var main = engine.Compile("SELECT id FROM main.s.t".AsMemory(), Request(), CancellationToken.None);
        Assert.Contains("`main`.`s`.`t`", main.Sql);
        Assert.ThrowsAny<System.Security.SecurityException>(() => engine.Compile("SELECT id FROM s.t".AsMemory(), Request(), CancellationToken.None));
    }
}
