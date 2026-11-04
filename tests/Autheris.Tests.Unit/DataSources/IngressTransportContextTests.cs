namespace Autheris.Tests.Unit.DataSources;

using System;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Shouldly;
using Xunit;

/// <summary>Issue #447 (SEC-ATR-02): the ingress context is set once, server-side, with exactly one defined transport.</summary>
[Trait("Category", "Security")]
public sealed class IngressTransportContextTests
{
    [Fact]
    public void Default_IsNone() => new IngressTransportContext().Current.ShouldBe(DataSourceTransportFlags.None);

    [Fact]
    public void Set_Once_ThenSameValue_NoOp()
    {
        var context = new IngressTransportContext();
        context.Set(DataSourceTransportFlags.Mcp);
        Should.NotThrow(() => context.Set(DataSourceTransportFlags.Mcp));
        context.Current.ShouldBe(DataSourceTransportFlags.Mcp);
    }

    [Fact]
    public void Set_DifferentValue_Throws_AndKeepsFirst()
    {
        var context = new IngressTransportContext();
        context.Set(DataSourceTransportFlags.Mcp);
        Should.Throw<InvalidOperationException>(() => context.Set(DataSourceTransportFlags.WebSql));
        context.Current.ShouldBe(DataSourceTransportFlags.Mcp);
    }

    [Fact]
    public void Set_None_Throws()
    {
        var context = new IngressTransportContext();
        Should.Throw<InvalidOperationException>(() => context.Set(DataSourceTransportFlags.None));
        context.Current.ShouldBe(DataSourceTransportFlags.None);
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.WebSql | DataSourceTransportFlags.GraphQL)]
    [InlineData(DataSourceTransportFlags.All)]
    [InlineData((DataSourceTransportFlags)(1 << 20))]
    public void Set_MultiBitOrUndefined_Throws(DataSourceTransportFlags value)
    {
        var context = new IngressTransportContext();
        Should.Throw<InvalidOperationException>(() => context.Set(value));
        context.Current.ShouldBe(DataSourceTransportFlags.None);
    }
}
