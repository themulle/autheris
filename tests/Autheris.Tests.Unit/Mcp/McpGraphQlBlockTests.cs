namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class McpGraphQlBlockTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static readonly IReadOnlyList<McpGraphQlArgument> ListArgs =
    [
        new("where", "orders_filter"), new("orderBy", "[orders_order_by!]"), new("first", "Int"), new("offset", "Int")
    ];

    private static GraphQlTableMapping Mapping(IReadOnlyList<string>? keys, bool withConnection = true) => new(
        Orders, "orders", "orders", "orders_filter", "orders_order_by", [], [],
        ListArguments: ListArgs,
        Aggregate: new GraphQlRootFieldSignature("orders_aggregate", "orders_aggregate_result", [new("aggregates", "[orders_aggregate_input!]!")]),
        Connection: withConnection
            ? new GraphQlRootFieldSignature("ordersConnection", "ordersConnection", [new("first", "Int"), new("after", "String")])
            : null,
        KeysetColumns: keys);

    private static McpDatasetColumn Column(string name, string? access, string? field = "") =>
        new(name, "int", null, false, false, field == "" ? name + "Field" : field, "Int", Access: access);

    private static string Json(McpGraphQlConnection? c) => JsonSerializer.Serialize(c, McpResultJson.Options);

    [Fact]
    public void Connection_NoKey_UnavailableNoUsableKey()
    {
        var result = McpGraphQlBlock.Connection(Mapping([]), [Column("a", "clear")])!;

        result.Available.ShouldBeFalse();
        result.Reason.ShouldBe("no_usable_key");
        result.KeyFields.ShouldBeNull();
        result.Field.ShouldBe("ordersConnection");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_NoKey_And_HiddenKey_AreByteIdentical()
    {
        var keyless = McpGraphQlBlock.Connection(Mapping(null), [Column("secret_id", null)]);
        var denied = McpGraphQlBlock.Connection(Mapping(["secret_id"]), [Column("secret_id", null)]);

        Json(denied).ShouldBe(Json(keyless));
        Json(denied).ShouldNotContain("keyFields");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_KeyColumnHidden_UnavailableNoUsableKey_NoColumnName()
    {
        var result = McpGraphQlBlock.Connection(Mapping(["secret_id"]), [Column("other", "clear")]);

        result!.Reason.ShouldBe("no_usable_key");
        Json(result).ShouldNotContain("secret_id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_KeyColumnWithoutGraphQlField_IsNoUsableKey()
    {
        var result = McpGraphQlBlock.Connection(Mapping(["id"]), [Column("id", "clear", field: null)]);

        result!.Reason.ShouldBe("no_usable_key");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_CompositeKey_OneHiddenOneMasked_IsNoUsableKey()
    {
        var result = McpGraphQlBlock.Connection(Mapping(["a", "b"]), [Column("a", "mask"), Column("b", null)]);

        result!.Reason.ShouldBe("no_usable_key");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_KeyColumnMasked_UnavailableMaskedKey_NoColumnName()
    {
        var result = McpGraphQlBlock.Connection(Mapping(["a", "b"]), [Column("a", "clear"), Column("b", "mask")]);

        result!.Available.ShouldBeFalse();
        result.Reason.ShouldBe("masked_key");
        result.KeyFields.ShouldBeNull();
        Json(result).ShouldNotContain("aField");
        Json(result).ShouldNotContain("\"b\"");
        Json(result).ShouldNotContain("bField");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_AllClear_KeyFieldsOnlyVisibleClearInOrder()
    {
        var columns = new[] { Column("z", "clear"), Column("a", "clear"), Column("x", "mask") };

        var result = McpGraphQlBlock.Connection(Mapping(["a", "z"]), columns);

        result!.Available.ShouldBeTrue();
        result.Reason.ShouldBeNull();
        result.KeyFields.ShouldBe(["aField", "zField"]);
        result.Variables.Select(v => v.Name).ShouldBe(["first", "after"]);
    }

    [Fact]
    public void Connection_UniqueIndexFallback_UsesIndexColumns()
    {
        // The mapping carries the resolved keyset columns (primary key, else first unique index); the block uses them as given.
        var result = McpGraphQlBlock.Connection(Mapping(["email"]), [Column("id", "clear"), Column("email", "clear")]);

        result!.Available.ShouldBeTrue();
        result.KeyFields.ShouldBe(["emailField"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_KeyWiderThanBound_OmitsKeyFields()
    {
        var names = Enumerable.Range(0, 17).Select(i => "k" + i).ToList();

        var result = McpGraphQlBlock.Connection(Mapping(names), names.Select(n => Column(n, "clear")).ToList());

        result!.Available.ShouldBeTrue();
        result.KeyFields.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Connection_NoConnectionSignature_IsOmittedNotAvailable()
    {
        McpGraphQlBlock.Connection(Mapping(["a"], withConnection: false), [Column("a", "clear")]).ShouldBeNull();
    }

    [Fact]
    public void Aggregate_NamesFieldResultAndVariables()
    {
        var result = McpGraphQlBlock.Aggregate(Mapping([]))!;

        result.Field.ShouldBe("orders_aggregate");
        result.ResultType.ShouldBe("orders_aggregate_result");
        result.Variables.Single().Type.ShouldBe("[orders_aggregate_input!]!");
        McpGraphQlBlock.Aggregate(Mapping([]) with { Aggregate = null }).ShouldBeNull();
    }
}
