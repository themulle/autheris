using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-4: static kinds, strict conversion, merged rows, counters (T-5.5, T-5.7, T-5.8).</summary>
public sealed class AggregateResultMapperTests
{
    private static readonly TableIdentifier Id = new("sales", "dbo", "orders");

    private static CatalogColumnField Col(string name, CatalogFieldType type) => new(name, name, type, type.ToString(), true);

    private static CatalogTableType TableOf(params CatalogColumnField[] cols) =>
        new(Id, "sales_dbo_orders", "sales_dbo_orders", "f", "o",
            new TableMetadata { Identifier = Id, Table = new Table { IsActive = true } }, cols, []);

    private static AggregationResult Result(IReadOnlyList<AggregationGroupRow> groups, long total = 0, AggregationMetadata? meta = null) =>
        new(Id, groups, new AggregationGroupRow(new Dictionary<string, object?>(), total, new Dictionary<string, object?>()),
            meta ?? new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false));

    private static AggregationGroupRow Row(Dictionary<string, object?> key, long count, Dictionary<string, object?> aggs, bool merged = false) =>
        new(key, count, aggs, merged);

    private static ResolvedAggregate Agg(string alias, AggregateFunction fn, CatalogColumnField? col)
    {
        var kind = AggregateResultMapper.KindFor(fn, col?.FieldType) ?? throw new InvalidOperationException("invalid combination");
        return new ResolvedAggregate(alias, fn, col, kind);
    }

    [Theory]
    [InlineData(AggregateFunction.Count, null, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Count, CatalogFieldType.String, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Count, CatalogFieldType.Boolean, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.CountDistinct, CatalogFieldType.String, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.CountDistinct, CatalogFieldType.DateTime, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.Int, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.Long, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.Decimal, AggregateValueKind.Decimal)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.Float, AggregateValueKind.Float)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.Int, AggregateValueKind.Decimal)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.Long, AggregateValueKind.Decimal)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.Decimal, AggregateValueKind.Decimal)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.Float, AggregateValueKind.Float)]
    [InlineData(AggregateFunction.Min, CatalogFieldType.Int, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Max, CatalogFieldType.Long, AggregateValueKind.Int)]
    [InlineData(AggregateFunction.Min, CatalogFieldType.Decimal, AggregateValueKind.Decimal)]
    [InlineData(AggregateFunction.Max, CatalogFieldType.Float, AggregateValueKind.Float)]
    [InlineData(AggregateFunction.Min, CatalogFieldType.DateTime, AggregateValueKind.DateTime)]
    [InlineData(AggregateFunction.Max, CatalogFieldType.String, AggregateValueKind.String)]
    public void KindFor_ValidCombinations_AreStatic(AggregateFunction fn, CatalogFieldType? column, AggregateValueKind expected) =>
        AggregateResultMapper.KindFor(fn, column).ShouldBe(expected);

    [Theory]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.Boolean)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.Boolean)]
    [InlineData(AggregateFunction.Min, CatalogFieldType.Boolean)]
    [InlineData(AggregateFunction.Max, CatalogFieldType.Boolean)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.String)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.String)]
    [InlineData(AggregateFunction.Sum, CatalogFieldType.DateTime)]
    [InlineData(AggregateFunction.Avg, CatalogFieldType.DateTime)]
    [InlineData(AggregateFunction.Sum, null)]
    [InlineData(AggregateFunction.Avg, null)]
    [InlineData(AggregateFunction.Min, null)]
    [InlineData(AggregateFunction.Max, null)]
    [InlineData(AggregateFunction.CountDistinct, null)]
    public void KindFor_InvalidCombinations_AreRejected(AggregateFunction fn, CatalogFieldType? column) =>
        AggregateResultMapper.KindFor(fn, column).ShouldBeNull();

    [Fact]
    public void Map_ConvertsDriverTypesToTheDeclaredKinds_InRequestOrder()
    {
        var amount = Col("amount", CatalogFieldType.Decimal);
        var qty = Col("qty", CatalogFieldType.Int);
        var price = Col("price", CatalogFieldType.Float);
        var seen = Col("seen", CatalogFieldType.DateTime);
        var name = Col("name", CatalogFieldType.String);
        var table = TableOf(amount, qty, price, seen, name);
        var aggs = new[]
        {
            Agg("total", AggregateFunction.Sum, amount), Agg("n", AggregateFunction.Count, null), Agg("q", AggregateFunction.Sum, qty),
            Agg("p", AggregateFunction.Avg, price), Agg("first_seen", AggregateFunction.Min, seen), Agg("last_name", AggregateFunction.Max, name)
        };
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var row = Row(new Dictionary<string, object?> { ["name"] = "a" }, 3, new Dictionary<string, object?>
        {
            ["total"] = 12.5m, ["n"] = (short)3, ["q"] = 7m, ["p"] = 1.25d, ["first_seen"] = new DateTimeOffset(when), ["last_name"] = "zed"
        });

        var dto = AggregateResultMapper.Map(Result([row], 3), table, [name], aggs);

        var values = dto.Groups.Single().Aggregates;
        values.Select(v => v.Alias).ShouldBe(["total", "n", "q", "p", "first_seen", "last_name"]);
        values[0].Kind.ShouldBe(AggregateValueKind.Decimal);
        values[0].DecimalValue.ShouldBe(12.5m);
        values[1].Kind.ShouldBe(AggregateValueKind.Int);
        values[1].IntValue.ShouldBe(3L);
        values[2].IntValue.ShouldBe(7L);
        values[3].Kind.ShouldBe(AggregateValueKind.Float);
        values[3].FloatValue.ShouldBe(1.25d);
        values[4].Kind.ShouldBe(AggregateValueKind.DateTime);
        values[4].DateTimeValue.ShouldBe(new DateTimeOffset(when));
        values[5].StringValue.ShouldBe("zed");
        dto.Groups.Single().Key["name"].ShouldBe("a");
        dto.Count.ShouldBe(3L);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(5L)]
    [InlineData((short)5)]
    [InlineData(5.0)]
    public void Map_IntKind_AcceptsIntegralDriverValues(object raw)
    {
        var qty = Col("qty", CatalogFieldType.Int);
        var dto = AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["s"] = raw })]),
            TableOf(qty), [], [Agg("s", AggregateFunction.Sum, qty)]);

        dto.Groups.Single().Aggregates.Single().IntValue.ShouldBe(5L);
    }

    [Fact]
    public void Map_NullAggregate_KeepsItsKind_AndHasNoValue()
    {
        var amount = Col("amount", CatalogFieldType.Decimal);
        var dto = AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["s"] = null })]),
            TableOf(amount), [], [Agg("s", AggregateFunction.Sum, amount)]);

        var value = dto.Groups.Single().Aggregates.Single();
        value.Kind.ShouldBe(AggregateValueKind.Decimal);
        value.DecimalValue.ShouldBeNull();
        value.IntValue.ShouldBeNull();
        value.FloatValue.ShouldBeNull();
        value.StringValue.ShouldBeNull();
        value.DateTimeValue.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Map_MergedOtherRow_HasNullKeysAndNullMinMaxCountDistinctAvg_SEC_112_07()
    {
        var qty = Col("qty", CatalogFieldType.Int);
        var created = Col("created", CatalogFieldType.DateTime);
        var name = Col("name", CatalogFieldType.String);
        var aggs = new[]
        {
            Agg("cd", AggregateFunction.CountDistinct, name), Agg("av", AggregateFunction.Avg, qty), Agg("mn", AggregateFunction.Min, qty),
            Agg("mx", AggregateFunction.Max, name), Agg("sm", AggregateFunction.Sum, qty), Agg("ct", AggregateFunction.Count, null)
        };
        var merged = Row(
            new Dictionary<string, object?> { ["qty"] = 4, ["created"] = DateTime.UtcNow, ["name"] = "leak" }, 9,
            new Dictionary<string, object?> { ["cd"] = 3L, ["av"] = 2.5m, ["mn"] = 1, ["mx"] = "leak", ["sm"] = 20m, ["ct"] = 9L },
            merged: true);

        var dto = AggregateResultMapper.Map(Result([merged], 9), TableOf(qty, created, name), [qty, created, name], aggs);

        var group = dto.Groups.Single();
        group.IsMergedOther.ShouldBeTrue();
        group.Key.Values.ShouldAllBe(v => v == null);
        group.Key.Keys.ShouldBe(["qty", "created", "name"], ignoreOrder: true);
        var byAlias = group.Aggregates.ToDictionary(a => a.Alias);
        byAlias["cd"].IntValue.ShouldBeNull();
        byAlias["av"].DecimalValue.ShouldBeNull();
        byAlias["mn"].IntValue.ShouldBeNull();
        byAlias["mx"].StringValue.ShouldBeNull();
        byAlias["sm"].IntValue.ShouldBe(20L);
        byAlias["ct"].IntValue.ShouldBe(9L);
    }

    [Theory]
    [InlineData(AggregateFunction.Min)]
    [InlineData(AggregateFunction.Max)]
    public void Map_DateOnlyAggregate_MapsToMidnightUtcLikeTheDateTextOfOtherEngines(AggregateFunction fn)
    {
        // Npgsql 10.0.3 returns System.DateOnly from GetValue for a PostgreSQL DATE (proven with a protocol-level probe); SQLite yields the text "2026-03-04".
        var saleDate = Col("sale_date", CatalogFieldType.DateTime);
        var expected = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);

        AggregateValueDto Run(object raw) => AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["d"] = raw })]),
            TableOf(saleDate), [], [Agg("d", fn, saleDate)]).Groups.Single().Aggregates.Single();

        Run(new DateOnly(2026, 3, 4)).DateTimeValue.ShouldBe(expected);
        Run("2026-03-04").DateTimeValue.ShouldBe(expected);
        Run(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified)).DateTimeValue.ShouldBe(expected);
    }

    [Fact]
    public void Map_DateOnlyGroupKey_IsTypedLikeSqliteDateText()
    {
        var saleDate = Col("sale_date", CatalogFieldType.DateTime);
        var dto = AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?> { ["sale_date"] = new DateOnly(2026, 3, 4) }, 1, new Dictionary<string, object?> { ["count"] = 1L })]),
            TableOf(saleDate), [saleDate], [Agg("count", AggregateFunction.Count, null)]);

        dto.Groups.Single().Key["sale_date"].ShouldBe("2026-03-04T00:00:00.0000000Z");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Map_TimeOnlyForDateTimeAggregate_StillRaisesTypeMismatch()
    {
        var saleDate = Col("sale_date", CatalogFieldType.DateTime);
        var ex = Should.Throw<GraphQLException>(() => AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["d"] = new TimeOnly(1, 2) })]),
            TableOf(saleDate), [], [Agg("d", AggregateFunction.Max, saleDate)]));
        ex.Errors.Single().Code.ShouldBe("AGGREGATION_RESULT_TYPE_MISMATCH");
        ex.Message.ShouldNotContain("01:02");
    }

    [Fact]
    public void Map_GroupKeys_AreTypedLikeTheEntityFields()
    {
        var qty = Col("qty", CatalogFieldType.Int);
        var big = Col("big", CatalogFieldType.Long);
        var flag = Col("flag", CatalogFieldType.Boolean);
        var created = Col("created", CatalogFieldType.DateTime);
        var price = Col("price", CatalogFieldType.Decimal);
        var ratio = Col("ratio", CatalogFieldType.Float);
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var key = new Dictionary<string, object?>
        {
            ["qty"] = (long)7, ["big"] = 8, ["flag"] = true, ["created"] = when, ["price"] = 3, ["ratio"] = 0.5m
        };

        var dto = AggregateResultMapper.Map(
            Result([Row(key, 1, new Dictionary<string, object?> { ["count"] = 1L })]),
            TableOf(qty, big, flag, created, price, ratio), [qty, big, flag, created, price, ratio], [Agg("count", AggregateFunction.Count, null)]);

        var k = dto.Groups.Single().Key;
        k["qty"].ShouldBe(7);
        k["big"].ShouldBe(8L);
        k["flag"].ShouldBe(true);
        k["created"].ShouldBe("2026-01-02T03:04:05.0000000Z");
        k["price"].ShouldBe(3m);
        k["ratio"].ShouldBe(0.5d);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("secret-value")]
    [InlineData(1.5)]
    public void Map_UnconvertibleValue_RaisesResultTypeMismatch_WithoutTheValue(object raw)
    {
        var qty = Col("qty", CatalogFieldType.Int);
        var ex = Should.Throw<GraphQLException>(() => AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["s"] = raw })]),
            TableOf(qty), [], [Agg("s", AggregateFunction.Sum, qty)]));

        var error = ex.Errors.Single();
        error.Code.ShouldBe(AggregationErrorCodes.ResultTypeMismatch);
        error.Message.ShouldNotContain("secret");
        error.Message.ShouldNotContain("1.5");
    }

    [Fact]
    public void Map_IntegerOverflow_RaisesResultTypeMismatch()
    {
        var qty = Col("qty", CatalogFieldType.Int);
        var ex = Should.Throw<GraphQLException>(() => AggregateResultMapper.Map(
            Result([Row(new Dictionary<string, object?> { ["qty"] = long.MaxValue }, 1, new Dictionary<string, object?> { ["count"] = 1L })]),
            TableOf(qty), [qty], [Agg("count", AggregateFunction.Count, null)]));

        ex.Errors.Single().Code.ShouldBe(AggregationErrorCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Map_CarriesCountersAndFlags_AndNeverTheSuppressedRowCount()
    {
        var meta = new AggregationMetadata(5, SmallGroupStrategy.Suppress, Suppressed: true, SuppressedGroupsCount: 3, SuppressedRowsCount: 7,
            MergedIntoOther: false, Truncated: true, NoiseApplied: true);

        var dto = AggregateResultMapper.Map(Result([], 12, meta), TableOf(Col("id", CatalogFieldType.Int)), [], [Agg("count", AggregateFunction.Count, null)]);

        dto.Suppressed.ShouldBeTrue();
        dto.SuppressedGroupsCount.ShouldBe(3);
        dto.MinimumGroupSize.ShouldBe(5);
        dto.Truncated.ShouldBeTrue();
        dto.NoiseApplied.ShouldBeTrue();
        dto.Count.ShouldBe(12L);
        typeof(AggregateResultDto).GetProperties().Select(p => p.Name).ShouldNotContain("SuppressedRowsCount");
    }
}
