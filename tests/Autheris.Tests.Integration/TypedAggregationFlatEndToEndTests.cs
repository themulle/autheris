namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// The flat <c>&lt;table&gt;_groups</c> root field and the nested <c>&lt;table&gt;_aggregate</c> field share one governed service, so the
/// same data and caller must give the same groups, values, order and facts over /graphql and the MCP <c>query_graphql</c> tool: k-anonymity
/// suppression, the OTHER merge, the optional suppressed-groups count, masked columns in every position, row-level security per caller,
/// truncation and the whole-table aggregate. Real consents and a real PostgreSQL data source; executed on CI only.
/// </summary>
public static class TypedAggregationFlatSupport
{
    public const string Sales = "default_main_flat_sales";
    public const string Masked = "default_main_flat_masked";
    public const string Air1 = "default_main_air1";
    public const string AnnaSid = "S-1-5-21-LWE-ANNA";

    private static readonly TableIdentifier SalesId = new("default", "main", "flat_sales");
    private static readonly TableIdentifier MaskedId = new("default", "main", "flat_masked");

    /// <summary>Everything the flat parity classes need: two data tables, their consents, and a second unrestricted caller on air1.</summary>
    public abstract class FlatFixture : RowFilterChannelParityTests.Fixture
    {
        protected override IReadOnlyCollection<string> RequiredGraphQlFields =>
        [
            .. base.RequiredGraphQlFields,
            Air1, Air1 + "_groups", Air1 + "_aggregate",
            Sales, Sales + "_groups", Sales + "_aggregate",
            Masked, Masked + "_groups", Masked + "_aggregate"
        ];

        protected abstract string SuppressionMode { get; }

        protected abstract bool ExposeSuppressedGroupsCount { get; }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Analytics:Privacy:SuppressionMode", SuppressionMode);
            builder.UseSetting("Analytics:Privacy:ExposeSuppressedGroupsCount", ExposeSuppressedGroupsCount ? "true" : "false");
        }

        // Groups: EU 12 rows, US 8, APAC 4 (below k = 5), LATAM 1 (below k).
        protected override void SeedExtraData(NpgsqlConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE flat_sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount INTEGER NOT NULL);
INSERT INTO flat_sales (id, region, amount)
SELECT row_number() OVER (), r, a FROM (
    SELECT 'EU' AS r, 10 AS a FROM generate_series(1, 12)
    UNION ALL SELECT 'US', 5 FROM generate_series(1, 8)
    UNION ALL SELECT 'APAC', 7 FROM generate_series(1, 4)
    UNION ALL SELECT 'LATAM', 3 FROM generate_series(1, 1)) AS t;
CREATE TABLE flat_masked (id INTEGER PRIMARY KEY, region TEXT, secret_amount INTEGER, secret_day TIMESTAMP);
INSERT INTO flat_masked VALUES
  (1,'EU',10,'2025-01-01'),(2,'EU',20,'2025-01-02'),(3,'EU',30,'2025-01-03'),
  (4,'EU',40,'2025-01-04'),(5,'EU',50,'2025-01-05'),(6,'EU',60,'2025-01-06');";
            cmd.ExecuteNonQuery();
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var sales = await RegisterTableAsync(repo, SalesId, ("id", "integer"), ("region", "varchar"), ("amount", "integer"));
            await repo.CreateConsentAsync(ReadersConsent(sales, SalesId, _ => ColumnAccessLevel.Clear));

            var masked = await RegisterTableAsync(
                repo, MaskedId, ("id", "integer"), ("region", "varchar"), ("secret_amount", "integer"), ("secret_day", "timestamp"));
            await repo.CreateConsentAsync(ReadersConsent(
                masked, MaskedId, column => column.StartsWith("secret_", StringComparison.Ordinal) ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear));

            // A second caller who reads air1 without the row filter that restricts David.
            var air1 = (await repo.GetTableMetadataAsync(new TableIdentifier("default", "main", "air1")))!;
            await repo.CreateConsentAsync(new Consent
            {
                TableId = air1.Table.Id,
                TableIdentifier = air1.Identifier,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(AnnaSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = air1.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
                RowFilters = []
            });
        }

        private static Consent ReadersConsent(TableMetadata meta, TableIdentifier id, Func<string, ColumnAccessLevel> level) => new()
        {
            TableId = meta.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(ReadersGroupSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = level(c.ColumnName) }).ToList(),
            RowFilters = []
        };
    }

    /// <summary>What a caller receives for one aggregation: the rows in order and every governed fact.</summary>
    public sealed record Outcome(
        IReadOnlyList<string> Rows,
        long Count,
        bool Suppressed,
        int SuppressedGroupsCount,
        int MinimumGroupSize,
        string? MinimumGroupSizeSource,
        bool MergedIntoOther,
        bool Truncated)
    {
        // Rows is a list, so the generated record equality would compare references; compare the rows by value instead.
        public bool Equals(Outcome? other) =>
            other is not null &&
            Rows.SequenceEqual(other.Rows) &&
            (Count, Suppressed, SuppressedGroupsCount, MinimumGroupSize, MinimumGroupSizeSource, MergedIntoOther, Truncated) ==
            (other.Count, other.Suppressed, other.SuppressedGroupsCount, other.MinimumGroupSize, other.MinimumGroupSizeSource, other.MergedIntoOther, other.Truncated);

        public override int GetHashCode() => HashCode.Combine(Rows.Count, Count, Suppressed, SuppressedGroupsCount, MinimumGroupSize, MergedIntoOther, Truncated);

        public override string ToString() =>
            $"Outcome {{ Rows = [{string.Join(", ", Rows)}], Count = {Count}, Suppressed = {Suppressed}, SuppressedGroupsCount = {SuppressedGroupsCount}, " +
            $"MinimumGroupSize = {MinimumGroupSize}, MinimumGroupSizeSource = {MinimumGroupSizeSource}, MergedIntoOther = {MergedIntoOther}, Truncated = {Truncated} }}";
    }

    // MIN/MAX/COUNT_DISTINCT on a non-group column are rejected under Suppress (minimum group size > 1), so the second aggregate is
    // COUNT unless the caller runs under MergeIntoOther and asks for MAX.
    public static string Nested(string table, string arguments = "groupBy: [region]", string where = "", string limit = "100", bool withMax = false) =>
        $$"""
        { {{table}}_aggregate({{arguments}}, aggregates: [{ fn: SUM, column: amount, alias: "s" }, {{(withMax ? "{ fn: MAX, column: amount, alias: \"m\" }" : "{ fn: COUNT, alias: \"m\" }")}}],
            orderBy: [{ column: region }], limit: {{limit}} {{where}}) {
            count suppressed suppressedGroupsCount minimumGroupSize minimumGroupSizeSource mergedIntoOther truncated
            groups { key { region } rowCount isMergedOther aggregates { alias intValue } } } }
        """;

    public static string Flat(string table, string where = "", string limit = "100", bool withMax = false) =>
        $"{{ {table}_groups(orderBy: [{{ column: region }}], limit: {limit} {where}) {{ region n: count s: amount_sum m: {(withMax ? "amount_max" : "count")} _isMergedOther }} }}";

    public static string NestedWhole(string table, string where = "") =>
        $$"""
        { {{table}}_aggregate(aggregates: [{ fn: COUNT, alias: "n" }] {{where}}) {
            count suppressed suppressedGroupsCount minimumGroupSize minimumGroupSizeSource mergedIntoOther truncated
            groups { rowCount aggregates { alias intValue } } } }
        """;

    public static string FlatWhole(string table, string where = "") =>
        $"{{ {table}_groups{(where.Length == 0 ? string.Empty : "(" + where.TrimStart(',', ' ') + ")")} {{ n: count }} }}";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()) : null;

    private static string Cell(JsonElement e) => e.ValueKind == JsonValueKind.Null ? "<null>" : e.ToString();

    public static Outcome ReadNested(JsonElement response, string table)
    {
        var data = response.GetProperty("data").GetProperty(table + "_aggregate");
        var rows = data.GetProperty("groups").EnumerateArray().Select(g =>
        {
            var values = g.GetProperty("aggregates").EnumerateArray().ToDictionary(a => a.GetProperty("alias").GetString()!, a => Cell(a.GetProperty("intValue")));
            var region = g.TryGetProperty("key", out var key) ? Cell(key.GetProperty("region")) : "-";
            return $"{region}|{g.GetProperty("rowCount")}|{values.GetValueOrDefault("s", "-")}|{values.GetValueOrDefault("m", "-")}|{g.GetProperty("isMergedOther")}";
        }).ToList();
        return Facts(rows, data);
    }

    public static Outcome ReadFlat(JsonElement response, string table)
    {
        var field = table + "_groups";
        var rows = response.GetProperty("data").GetProperty(field).EnumerateArray().Select(r =>
            $"{Cell(r.GetProperty("region"))}|{r.GetProperty("n")}|{Cell(r.GetProperty("s"))}|{Cell(r.GetProperty("m"))}|{r.GetProperty("_isMergedOther")}").ToList();
        return Facts(rows, response.GetProperty("extensions").GetProperty("autherisAggregation").GetProperty(field));
    }

    public static Outcome ReadNestedWhole(JsonElement response, string table)
    {
        var data = response.GetProperty("data").GetProperty(table + "_aggregate");
        var rows = data.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("rowCount").ToString()).ToList();
        return Facts(rows, data);
    }

    public static Outcome ReadFlatWhole(JsonElement response, string table)
    {
        var field = table + "_groups";
        var rows = response.GetProperty("data").GetProperty(field).EnumerateArray().Select(r => r.GetProperty("n").ToString()).ToList();
        return Facts(rows, response.GetProperty("extensions").GetProperty("autherisAggregation").GetProperty(field));
    }

    private static Outcome Facts(IReadOnlyList<string> rows, JsonElement facts) => new(
        rows,
        facts.GetProperty("count").GetInt64(),
        facts.GetProperty("suppressed").GetBoolean(),
        facts.GetProperty("suppressedGroupsCount").GetInt32(),
        facts.GetProperty("minimumGroupSize").GetInt32(),
        Str(facts, "minimumGroupSizeSource"),
        facts.GetProperty("mergedIntoOther").GetBoolean(),
        facts.GetProperty("truncated").GetBoolean());

    /// <summary>Raw /graphql response (errors do not throw).</summary>
    public static async Task<JsonDocument> RawGraphQlAsync(RowFilterChannelParityTests.Fixture fixture, string query)
    {
        var client = fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public static async Task<JsonDocument> GraphQlAsync(RowFilterChannelParityTests.Fixture fixture, string query)
    {
        var doc = await RawGraphQlAsync(fixture, query);
        doc.RootElement.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        return doc;
    }

    public static async Task<JsonElement> McpAsync(RowFilterChannelParityTests.Fixture fixture, string query)
    {
        await using var mcp = await McpTestClient.ConnectAsync(fixture.Client());
        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query });
        isError.ShouldBeFalse(payload.ToString());
        return payload;
    }

    public static string ErrorCode(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("errors", out var errors)
            ? errors[0].GetProperty("extensions").GetProperty("code").GetString()!
            : "<no error>";

    /// <summary>Runs <paramref name="action"/> as another caller and restores the default caller afterwards.</summary>
    public static async Task AsCallerAsync(RowFilterChannelParityTests.Fixture fixture, string sid, Func<Task> action)
    {
        var previous = fixture.CallerSid;
        fixture.CallerSid = sid;
        try
        {
            await action();
        }
        finally
        {
            fixture.CallerSid = previous;
        }
    }
}

/// <summary>Default privacy options: small groups are suppressed and their count stays hidden.</summary>
[Trait("Category", "Security")]
public sealed class TypedAggregationFlatEndToEndTests : IClassFixture<TypedAggregationFlatEndToEndTests.Fixture>
{
    private readonly Fixture _fixture;

    public TypedAggregationFlatEndToEndTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : TypedAggregationFlatSupport.FlatFixture
    {
        protected override string SuppressionMode => "Suppress";

        protected override bool ExposeSuppressedGroupsCount => false;
    }

    [Fact]
    public async Task NestedAndFlat_Parity_KAnonSuppression_Graphql()
    {
        using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Nested(TypedAggregationFlatSupport.Sales));
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales));

        var expected = TypedAggregationFlatSupport.ReadNested(nested.RootElement, TypedAggregationFlatSupport.Sales);
        var actual = TypedAggregationFlatSupport.ReadFlat(flat.RootElement, TypedAggregationFlatSupport.Sales);

        actual.Rows.ShouldBe(expected.Rows);
        actual.Rows.Count.ShouldBe(2);
        actual.Rows[0].ShouldStartWith("EU|12|120|12|");
        actual.ShouldBe(expected, "facts must be identical, including the hidden suppressed-groups count");
        actual.Suppressed.ShouldBeTrue();
        actual.SuppressedGroupsCount.ShouldBe(0);
        actual.Count.ShouldBe(20);
    }

    [Fact]
    public async Task NestedAndFlat_Parity_KAnonSuppression_ViaMcpQueryGraphql()
    {
        var nested = await TypedAggregationFlatSupport.McpAsync(_fixture, TypedAggregationFlatSupport.Nested(TypedAggregationFlatSupport.Sales));
        var flat = await TypedAggregationFlatSupport.McpAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales));

        TypedAggregationFlatSupport.ReadFlat(flat, TypedAggregationFlatSupport.Sales)
            .ShouldBe(TypedAggregationFlatSupport.ReadNested(nested, TypedAggregationFlatSupport.Sales));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("100")]
    public async Task NestedAndFlat_Parity_Truncated_SameFact(string limit)
    {
        using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Nested(TypedAggregationFlatSupport.Sales, limit: limit));
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales, limit: limit));

        var expected = TypedAggregationFlatSupport.ReadNested(nested.RootElement, TypedAggregationFlatSupport.Sales);
        var actual = TypedAggregationFlatSupport.ReadFlat(flat.RootElement, TypedAggregationFlatSupport.Sales);

        actual.Truncated.ShouldBe(expected.Truncated);
        actual.ShouldBe(expected);
        (limit == "1").ShouldBe(actual.Truncated);
    }

    [Fact]
    public async Task NestedAndFlat_Parity_SuppressedGroupsCount_HiddenWhenExposeOff()
    {
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales));

        var facts = flat.RootElement.GetProperty("extensions").GetProperty("autherisAggregation").GetProperty(TypedAggregationFlatSupport.Sales + "_groups");
        facts.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(0);
        facts.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["count", "suppressed", "suppressedGroupsCount", "minimumGroupSize", "minimumGroupSizeSource", "mergedIntoOther", "truncated"], ignoreOrder: true);
    }

    [Fact]
    public async Task NestedAndFlat_Parity_ResponseKeysAndAliases_DoNotChangeTheResult()
    {
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture,
            $"{{ werte: {TypedAggregationFlatSupport.Sales}_groups(orderBy: [{{ column: region }}]) {{ gebiet: region anzahl: count summe: amount_sum schnitt: amount_avg _isMergedOther }} }}");

        var rows = flat.RootElement.GetProperty("data").GetProperty("werte").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("gebiet").GetString()).ShouldBe(["EU", "US"]);
        rows.Select(r => r.GetProperty("anzahl").GetInt64()).ShouldBe([12, 8]);
        flat.RootElement.GetProperty("extensions").GetProperty("autherisAggregation").TryGetProperty("werte", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("group", "groupBy: [secret_amount]", "{ secret_amount n: count }", "")]
    [InlineData("where", "groupBy: [region]", "{ region n: count }", ", where: { secret_amount: { gt: 1 } }")]
    [InlineData("aggregate", "aggregates: [{ fn: MIN, column: secret_amount, alias: \"m\" }]", "{ secret_amount_min }", "")]
    [InlineData("bucket", "timeBuckets: [{ column: secret_day, bucket: DAY, timeZone: \"UTC\" }]", "{ secret_day(bucket: DAY, timeZone: \"UTC\") n: count }", "")]
    public async Task NestedAndFlat_Parity_Masks_GroupWhereAggregateBucket_SameCode(string position, string nestedArguments, string flatSelection, string where)
    {
        var table = TypedAggregationFlatSupport.Masked;
        var aggregates = nestedArguments.StartsWith("aggregates", StringComparison.Ordinal) ? string.Empty : ", aggregates: [{ fn: COUNT, alias: \"n\" }]";
        using var nested = await TypedAggregationFlatSupport.RawGraphQlAsync(_fixture, $"{{ {table}_aggregate({nestedArguments}{aggregates}{where}) {{ count }} }}");
        var flatWhere = where.TrimStart(',', ' ');
        using var flat = await TypedAggregationFlatSupport.RawGraphQlAsync(_fixture,
            $"{{ {table}_groups{(flatWhere.Length == 0 ? string.Empty : "(" + flatWhere + ")")} {flatSelection} }}");

        var nestedCode = TypedAggregationFlatSupport.ErrorCode(nested);
        TypedAggregationFlatSupport.ErrorCode(flat).ShouldBe(nestedCode, position);
        nestedCode.ShouldBe("AGGREGATION_ON_MASKED_COLUMN", position);
        flat.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()!.ShouldNotContain("secret_");
    }

    [Fact]
    public async Task NestedAndFlat_Parity_Masks_OrderPosition_SameCode()
    {
        var table = TypedAggregationFlatSupport.Masked;
        using var nested = await TypedAggregationFlatSupport.RawGraphQlAsync(_fixture,
            $"{{ {table}_aggregate(groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: secret_amount }}]) {{ count }} }}");
        using var flat = await TypedAggregationFlatSupport.RawGraphQlAsync(_fixture,
            $"{{ {table}_groups(orderBy: [{{ column: secret_amount }}]) {{ region n: count }} }}");

        TypedAggregationFlatSupport.ErrorCode(nested).ShouldNotBe("<no error>");
        TypedAggregationFlatSupport.ErrorCode(flat).ShouldBe(TypedAggregationFlatSupport.ErrorCode(nested));
    }

    [Fact]
    public async Task NestedAndFlat_Parity_ClearColumnsOfAMaskedTable_AreAggregatable()
    {
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture,
            $"{{ {TypedAggregationFlatSupport.Masked}_groups {{ region n: count }} }}");

        var row = flat.RootElement.GetProperty("data").GetProperty(TypedAggregationFlatSupport.Masked + "_groups").EnumerateArray().ShouldHaveSingleItem();
        row.GetProperty("region").GetString().ShouldBe("EU");
        row.GetProperty("n").GetInt64().ShouldBe(6);
    }

    [Fact]
    public async Task NestedAndFlat_Parity_RowFilterRestrictedCaller_SameGroups()
    {
        async Task AssertCallerAsync(string sid, long expectedRows)
        {
            await TypedAggregationFlatSupport.AsCallerAsync(_fixture, sid, async () =>
            {
                using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.NestedWhole(TypedAggregationFlatSupport.Air1));
                using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.FlatWhole(TypedAggregationFlatSupport.Air1));

                var expected = TypedAggregationFlatSupport.ReadNestedWhole(nested.RootElement, TypedAggregationFlatSupport.Air1);
                var actual = TypedAggregationFlatSupport.ReadFlatWhole(flat.RootElement, TypedAggregationFlatSupport.Air1);

                actual.ShouldBe(expected, sid);
                actual.Rows.ShouldBe([expectedRows.ToString(System.Globalization.CultureInfo.InvariantCulture)], sid);
            });
        }

        await AssertCallerAsync(RowFilterChannelParityTests.Fixture.DavidSid, _fixture.ExpectedIds.Count);
        await AssertCallerAsync(TypedAggregationFlatSupport.AnnaSid, 13);
    }

    [Fact]
    public async Task WholeTableAggregate_BelowMinGroupSize_SuppressedLikeNested()
    {
        const string where = ", where: { region: { eq: \"LATAM\" } }";
        using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.NestedWhole(TypedAggregationFlatSupport.Sales, where));
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.FlatWhole(TypedAggregationFlatSupport.Sales, where));

        var expected = TypedAggregationFlatSupport.ReadNestedWhole(nested.RootElement, TypedAggregationFlatSupport.Sales);
        var actual = TypedAggregationFlatSupport.ReadFlatWhole(flat.RootElement, TypedAggregationFlatSupport.Sales);

        actual.ShouldBe(expected);
        actual.Suppressed.ShouldBeTrue();
        actual.Rows.ShouldBeEmpty();
    }
}

/// <summary>ExposeSuppressedGroupsCount on: the flat facts show the number of suppressed groups exactly like the nested field.</summary>
[Trait("Category", "Security")]
public sealed class TypedAggregationFlatExposeCountEndToEndTests : IClassFixture<TypedAggregationFlatExposeCountEndToEndTests.Fixture>
{
    private readonly Fixture _fixture;

    public TypedAggregationFlatExposeCountEndToEndTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : TypedAggregationFlatSupport.FlatFixture
    {
        protected override string SuppressionMode => "Suppress";

        protected override bool ExposeSuppressedGroupsCount => true;
    }

    [Fact]
    public async Task NestedAndFlat_Parity_SuppressedGroupsCount_ShownWhenExposeOn()
    {
        using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Nested(TypedAggregationFlatSupport.Sales));
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales));

        var expected = TypedAggregationFlatSupport.ReadNested(nested.RootElement, TypedAggregationFlatSupport.Sales);
        var actual = TypedAggregationFlatSupport.ReadFlat(flat.RootElement, TypedAggregationFlatSupport.Sales);

        actual.ShouldBe(expected);
        actual.SuppressedGroupsCount.ShouldBe(2);
    }
}

/// <summary>Merge strategy: small groups join one OTHER row; the flat row carries the marker and null non-mergeable aggregates.</summary>
[Trait("Category", "Security")]
public sealed class TypedAggregationFlatMergeEndToEndTests : IClassFixture<TypedAggregationFlatMergeEndToEndTests.Fixture>
{
    private readonly Fixture _fixture;

    public TypedAggregationFlatMergeEndToEndTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : TypedAggregationFlatSupport.FlatFixture
    {
        protected override string SuppressionMode => "MergeIntoOther";

        protected override bool ExposeSuppressedGroupsCount => false;
    }

    [Fact]
    public async Task NestedAndFlat_Parity_MergeIntoOther_OtherRowNulls()
    {
        using var nested = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Nested(TypedAggregationFlatSupport.Sales, withMax: true));
        using var flat = await TypedAggregationFlatSupport.GraphQlAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales, withMax: true));
        var viaMcp = await TypedAggregationFlatSupport.McpAsync(_fixture, TypedAggregationFlatSupport.Flat(TypedAggregationFlatSupport.Sales, withMax: true));

        var expected = TypedAggregationFlatSupport.ReadNested(nested.RootElement, TypedAggregationFlatSupport.Sales);
        var actual = TypedAggregationFlatSupport.ReadFlat(flat.RootElement, TypedAggregationFlatSupport.Sales);

        actual.ShouldBe(expected);
        TypedAggregationFlatSupport.ReadFlat(viaMcp, TypedAggregationFlatSupport.Sales).ShouldBe(expected);
        actual.MergedIntoOther.ShouldBeTrue();
        var other = actual.Rows.Single(r => r.EndsWith("|True", StringComparison.Ordinal));
        other.ShouldStartWith("<null>|5|");
        other.Split('|')[3].ShouldBe("<null>", "MAX is not mergeable and must be null on the OTHER row");
    }
}
