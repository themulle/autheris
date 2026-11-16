using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the client list value and its value-free shape (REQ-SQL-128): an element must be non-null and have
/// exactly the CLR type of the list's element type, the value is copied, a type that cannot be a list element is refused, the shape
/// accepts only plain names and non-negative counts, and the canonical shape (part of the compile cache key) is exact and ordered.
/// Rejections carry only the stable code and its fixed message, never a value, name or count.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class ClientListValueAndShapeRuleTests
{
    public static IEnumerable<object[]> ElementTypes() =>
    [
        [SqlParameterType.String, "a", typeof(string)],
        [SqlParameterType.Int32, 1, typeof(int)],
        [SqlParameterType.Int64, 1L, typeof(long)],
        [SqlParameterType.Decimal, 1.5m, typeof(decimal)],
        [SqlParameterType.Double, 1.5d, typeof(double)],
        [SqlParameterType.Boolean, true, typeof(bool)],
        [SqlParameterType.Date, new DateOnly(2026, 1, 2), typeof(DateOnly)],
        [SqlParameterType.Timestamp, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), typeof(DateTime)],
        [SqlParameterType.TimestampTz, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), typeof(DateTimeOffset)]
    ];

    [Theory]
    [MemberData(nameof(ElementTypes))]
    public void EveryListElementType_AcceptsExactlyItsClrType(SqlParameterType type, object sample, Type clrType)
    {
        Assert.Equal(clrType, ClientListValue.ClrTypeOf(type));
        Assert.Equal(clrType, sample.GetType());

        var value = ClientListValue.Create(new[] { sample, sample }, type);

        Assert.Equal(type, value.ElementType);
        Assert.Equal(2, value.Count);
        Assert.Equal(sample, value[0]);
        Assert.Equal(sample, value[1]);
        Assert.Equal($"ClientListValue {{ ElementType = {type}, Count = 2 }}", value.ToString());

        // another CLR type of the same list is refused with the type code
        object other = clrType == typeof(string) ? 1 : "x";
        var ex = Assert.Throws<ClientListException>(() => ClientListValue.Create(new[] { sample, other }, type));
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, ex.Code);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.ListParamType), ex.Message);
    }

    [Theory]
    [InlineData(SqlParameterType.Time)]
    [InlineData(SqlParameterType.Binary)]
    [InlineData(SqlParameterType.Null)]
    public void NonListElementTypes_AreRefused(SqlParameterType type)
    {
        Assert.Null(ClientListValue.ClrTypeOf(type));
        var ex = Assert.Throws<ClientListException>(() => ClientListValue.Create(new object[] { "x" }, type));
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, ex.Code);
        Assert.Throws<ClientListException>(() => ClientListValue.Create(Array.Empty<object>(), type));
    }

    [Fact]
    public void NullElement_IsRefusedWithTheNullCode_EvenAfterValidElements()
    {
        var ex = Assert.Throws<ClientListException>(() => ClientListValue.Create(new object?[] { "a", null }!, SqlParameterType.String));
        Assert.Equal(GovernedSqlErrorCodes.ListParamNull, ex.Code);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.ListParamNull), ex.Message);
        Assert.Equal(GovernedSqlErrorCodes.ListParamNull,
            Assert.Throws<ClientListException>(() => ClientListValue.Create(new object?[] { null }!, SqlParameterType.Int32)).Code);
        // the first offending element decides the code
        Assert.Equal(GovernedSqlErrorCodes.ListParamType,
            Assert.Throws<ClientListException>(() => ClientListValue.Create(new object?[] { 1, null }!, SqlParameterType.String)).Code);
    }

    [Fact]
    public void Value_IsCopiedAndTheErrorsCarryNoValue()
    {
        var source = new List<object> { "a", "b" };
        var value = ClientListValue.Create(source, SqlParameterType.String);
        source[0] = "changed";
        source.Add("c");

        Assert.Equal(2, value.Count);
        Assert.Equal("a", value[0]);
        Assert.Equal("b", value[1]);
        Assert.DoesNotContain("a", value.ToString().Replace("ClientListValue", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0, ClientListValue.Create(Array.Empty<object>(), SqlParameterType.Int32).Count);
        Assert.Throws<ArgumentNullException>(() => ClientListValue.Create(null!, SqlParameterType.String));
    }

    [Fact]
    public void ListException_IsACallerMistakeWithTheFixedMessage()
    {
        var ex = new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal(GovernedSqlErrorCodes.ListParamTooLarge, ex.Code);
        Assert.Equal("A list parameter exceeds the maximum number of values.", ex.Message);
    }

    [Fact]
    public void Shapes_AreCanonicalLowerCasedOrderedAndCaseInsensitive()
    {
        var shapes = ClientListShapes.Create(new Dictionary<string, int> { ["Zeta"] = 3, ["alpha"] = 12, ["_b9"] = 0 });

        Assert.Equal("_b9:0;alpha:12;zeta:3;", shapes.Canonical);
        Assert.Equal(12, shapes.MaxCount());
        Assert.True(shapes.TryGetCount("ALPHA", out int alpha));
        Assert.Equal(12, alpha);
        Assert.True(shapes.TryGetCount("zeta", out int zeta));
        Assert.Equal(3, zeta);
        Assert.False(shapes.TryGetCount("missing", out int none));
        Assert.Equal(0, none);
        Assert.False(shapes.IsEmpty);
    }

    [Fact]
    public void EmptyShape_IsTheSharedEmptyInstance()
    {
        var empty = ClientListShapes.Create(new Dictionary<string, int>());

        Assert.Same(ClientListShapes.Empty, empty);
        Assert.Equal(string.Empty, empty.Canonical);
        Assert.Equal(0, empty.MaxCount());
        Assert.True(empty.IsEmpty);
        Assert.False(empty.TryGetCount("a", out _));
        Assert.Throws<ArgumentNullException>(() => ClientListShapes.Create(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("a:b")]
    [InlineData("a;b")]
    [InlineData("a\n")]
    [InlineData("ä")]
    public void NamesOutsideThePlainIdentifierShape_AreRefused(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => ClientListShapes.Create(new Dictionary<string, int> { [name] = 1 }));
        Assert.Equal("expandedCounts", ex.ParamName);
        Assert.StartsWith("Invalid client list shape.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NameLength_IsBoundedAt128Characters()
    {
        string longest = "a" + new string('b', 127);
        Assert.Equal(128, longest.Length);
        Assert.Equal(longest + ":1;", ClientListShapes.Create(new Dictionary<string, int> { [longest] = 1 }).Canonical);
        Assert.Throws<ArgumentException>(() => ClientListShapes.Create(new Dictionary<string, int> { [longest + "c"] = 1 }));
    }

    [Fact]
    public void NegativeCount_IsRefused_ZeroAndLargeCountsAreNot()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClientListShapes.Create(new Dictionary<string, int> { ["a"] = -1 }));
        Assert.StartsWith("Invalid client list shape.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("a:0;", ClientListShapes.Create(new Dictionary<string, int> { ["a"] = 0 }).Canonical);
        Assert.Equal("a:2147483647;", ClientListShapes.Create(new Dictionary<string, int> { ["a"] = int.MaxValue }).Canonical);
    }

    [Fact]
    public void NamesDifferingOnlyByCase_AreADuplicate()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClientListShapes.Create(new Dictionary<string, int> { ["List"] = 1, ["LIST"] = 2 }));
        Assert.StartsWith("Duplicate client list shape.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("expandedCounts", ex.ParamName);
    }
}
