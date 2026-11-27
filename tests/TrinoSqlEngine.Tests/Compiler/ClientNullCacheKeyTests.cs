using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>The null shape (names only, never values) is part of the template cache key.</summary>
[Trait("Category", "SqlGate")]
public class ClientNullCacheKeyTests
{
    private const string Sql = "SELECT id FROM orders WHERE id = __param_p";

    private readonly ClientListHarness _h = new();

    private static string Key(CompileRequest request) => CompileCacheKey.Material(request, Sql, TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(request.TargetDialect));

    private CompileRequest Request(ClientNullShapes nulls) => _h.Request(TargetSqlDialect.PostgreSql) with { ClientNulls = nulls };

    [Fact]
    [Trait("Category", "Security")]
    public void Null_and_value_shapes_get_different_keys()
    {
        Assert.NotEqual(Key(Request(ClientNullShapes.Empty)), Key(Request(ClientNullShapes.Create(["p"]))));
        Assert.NotEqual(Key(Request(ClientNullShapes.Create(["p"]))), Key(Request(ClientNullShapes.Create(["p", "q"]))));
    }

    [Fact]
    public void Key_is_independent_of_name_case_and_order()
    {
        Assert.Equal(Key(Request(ClientNullShapes.Create(["P", "q"]))), Key(Request(ClientNullShapes.Create(["Q", "p"]))));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Key_contains_names_only_never_values()
    {
        var key = Key(Request(ClientNullShapes.Create(["region"])));

        Assert.Contains("region;", key, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", key, StringComparison.Ordinal);
        Assert.Equal("region;", ClientNullShapes.Create(["region"]).Canonical);
    }
}
