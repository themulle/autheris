using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Autheris.Tests.Unit.Sql;

/// <summary>#668: <c>SqlDataSourceResolver.ResolveConnectionKey</c> is the one place that applies <c>WebSql:DataSourceMappings</c>.</summary>
public sealed class SqlDataSourceResolverMappingTests
{
    private static SqlDataSourceResolver Build(Action<GatewayOptions> configure, params string[] connections)
    {
        var gateway = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } };
        foreach (var name in connections)
        {
            gateway.DataSources.Connections[name] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "x" };
        }

        configure(gateway);
        return new SqlDataSourceResolver(Options.Create(gateway));
    }

    [Fact]
    public void ResolveConnectionKey_Mapped_ReturnsTarget()
    {
        var sut = Build(o => o.WebSql.DataSourceMappings["sales"] = "PGDATA", "pgdata");

        sut.ResolveConnectionKey("sales").ShouldBe("pgdata");   // the key as spelled in DataSources:Connections
        sut.ResolveConnectionKey("SALES").ShouldBe("pgdata");
    }

    [Fact]
    public void ResolveConnectionKey_Unmapped_IsTheNameWhenAConnectionExists()
    {
        var sut = Build(_ => { }, "pgdata");

        sut.ResolveConnectionKey("pgdata").ShouldBe("pgdata");
        sut.ResolveConnectionKey("nope").ShouldBeNull();
        sut.ResolveConnectionKey(null).ShouldBeNull();
        sut.ResolveConnectionKey(" ").ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown")]
    [Trait("Category", "Security")]
    public void ResolveConnectionKey_EmptyOrUnknownTarget_ResolvesToNull(string target)
    {
        var sut = Build(o => o.WebSql.DataSourceMappings["sales"] = target, "pgdata", "sales");

        // "sales" is itself a connection: a broken mapping must not fall back to it
        sut.ResolveConnectionKey("sales").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ResolveConnectionKey_TargetThatIsAlsoAMappingKey_ResolvesToNull()
    {
        var sut = Build(o =>
        {
            o.WebSql.DataSourceMappings["a"] = "b";
            o.WebSql.DataSourceMappings["b"] = "c";
        }, "a", "b", "c");

        sut.ResolveConnectionKey("a").ShouldBeNull();   // chain: one hop only
        sut.ResolveConnectionKey("b").ShouldBe("c");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ResolveConnectionKey_CycleAndSelfMapping_ResolveToNull()
    {
        var sut = Build(o =>
        {
            o.WebSql.DataSourceMappings["a"] = "b";
            o.WebSql.DataSourceMappings["b"] = "a";
            o.WebSql.DataSourceMappings["s"] = "s";
        }, "a", "b", "s");

        sut.ResolveConnectionKey("a").ShouldBeNull();
        sut.ResolveConnectionKey("b").ShouldBeNull();
        sut.ResolveConnectionKey("s").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ResolveConnectionKey_MappedName_NotInAllowedDataSources_StillDenied()
    {
        var sut = Build(o => o.WebSql.DataSourceMappings["sales"] = "pgdata", "pgdata");

        // the mapping target is a connection key, not an allowed data source name
        sut.TryResolveAllowedDataSource("pgdata", new TenantId("t"), out _).ShouldBeFalse();
        sut.TryResolveAllowedDataSource("sales", new TenantId("t"), out var resolved).ShouldBeTrue();
        resolved.ShouldBe("sales");   // the logical name stays the allowlist and catalog name
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TenantAllowlist_AppliesToLogicalName()
    {
        var sut = Build(o =>
        {
            o.WebSql.DataSourceMappings["sales"] = "pgdata";
            o.WebSql.TenantDataSourceAllowlist["tenant_a"] = ["sales"];
            o.WebSql.TenantDataSourceAllowlist["tenant_b"] = ["pgdata"];
        }, "pgdata");

        sut.TryResolveAllowedDataSource("sales", new TenantId("tenant_a"), out _).ShouldBeTrue();
        sut.TryResolveAllowedDataSource("sales", new TenantId("tenant_b"), out _).ShouldBeFalse();
    }
}
