using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

public class ClientNullShapesTests
{
    [Fact]
    public void Empty_has_no_names()
    {
        Assert.Equal(string.Empty, ClientNullShapes.Empty.Canonical);
        Assert.Same(ClientNullShapes.Empty, ClientNullShapes.Create([]));
        Assert.False(ClientNullShapes.Empty.Contains("p"));
    }

    [Fact]
    public void Names_are_lower_cased_distinct_and_sorted()
    {
        var shapes = ClientNullShapes.Create(["Zed", "alpha", "ALPHA"]);

        Assert.Equal("alpha;zed;", shapes.Canonical);
        Assert.True(shapes.Contains("ALPHA"));
        Assert.False(shapes.Contains("other"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1a")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a:1")]
    [InlineData("a'--")]
    [Trait("Category", "Security")]
    public void Invalid_null_shape_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => ClientNullShapes.Create([name]));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Invalid_null_shape_name_is_refused_for_overlong_names()
    {
        Assert.Throws<ArgumentException>(() => ClientNullShapes.Create([new string('a', 129)]));
    }
}
