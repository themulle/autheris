using System.Text.Json;
using Autheris.Application.Sql.ListParameters;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Sql.ListParameters;

public class ClientListParametersTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ClientListValue List(params long[] values) => ClientListValue.Create(values.Select(v => (object)v).ToList(), SqlParameterType.Int64);

    [Fact]
    [Trait("Category", "Security")]
    public void Shapes_ContainReferencedListsOnly_UnreferencedAreDropped()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ids"] = List(1, 2, 3), ["unused"] = List(1, 2), ["scalar"] = "x"
        };
        var shapes = ClientListParameters.Shapes(values, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ids", "scalar" });

        shapes.TryGetCount("ids", out int count).ShouldBeTrue();
        count.ShouldBe(3);
        shapes.TryGetCount("unused", out _).ShouldBeFalse();
        shapes.TryGetCount("scalar", out _).ShouldBeFalse();
        shapes.Canonical.ShouldBe("ids:3;");
    }

    [Fact]
    public void Shapes_NoLists_IsEmpty()
    {
        ClientListParameters.Shapes(new Dictionary<string, object?> { ["a"] = 1L }, new HashSet<string> { "a" }).Canonical.ShouldBe(string.Empty);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Shapes_MixedCaseSpellings_YieldOneShape()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["IDS"] = List(1, 2) };
        var shapes = ClientListParameters.Shapes(values, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ids", "Ids" });
        shapes.Canonical.ShouldBe("ids:2;");
    }

    // SEC-128-12 / M-10 (raw parameters, not normalized values)
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[]")]
    [Trait("Category", "Security")]
    public void RejectForFederation_RawJsonArray_IsRefused(string json)
    {
        var ex = Should.Throw<ClientListException>(() => ClientListParameters.RejectForFederation(new Dictionary<string, object?> { ["x"] = Json(json) }));
        ex.Code.ShouldBe("SQL_LIST_PARAM_UNSUPPORTED");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RejectForFederation_CollectionsAndClientListValues_AreRefused()
    {
        Should.Throw<ClientListException>(() => ClientListParameters.RejectForFederation(new Dictionary<string, object?> { ["x"] = new[] { "a" } }));
        Should.Throw<ClientListException>(() => ClientListParameters.RejectForFederation(new Dictionary<string, object?> { ["x"] = new List<object?>() }));
        Should.Throw<ClientListException>(() => ClientListParameters.RejectForFederation(new Dictionary<string, object?> { ["x"] = List(1) }));
    }

    [Fact]
    public void RejectForFederation_ScalarsAndNull_Pass()
    {
        ClientListParameters.RejectForFederation(null);
        ClientListParameters.RejectForFederation(new Dictionary<string, object?>());
        ClientListParameters.RejectForFederation(new Dictionary<string, object?> { ["a"] = "x", ["b"] = 1L, ["c"] = Json("5"), ["d"] = null });
    }
}
