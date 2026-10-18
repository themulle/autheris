namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

/// <summary>
/// #112 T-5.10 / T-5.15 / T-6.1: the typed aggregate field obeys the same governance as every other channel, with real consents and
/// real SQLite data. The restricted caller (row filter on air1) counts only the rows it may read; a column masked by consent cannot be
/// aggregated, grouped or filtered on.
/// </summary>
[Trait("Category", "Security")]
public sealed class TypedAggregationParityEndToEndTests : IClassFixture<TypedAggregationParityEndToEndTests.AggregateFixture>
{
    private readonly AggregateFixture _fixture;

    public TypedAggregationParityEndToEndTests(AggregateFixture fixture) => _fixture = fixture;

    public sealed class AggregateFixture : RowFilterChannelParityTests.Fixture
    {
        private static readonly TableIdentifier MaskedId = new("default", "main", "masked_t");

        protected override void SeedExtraData(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE masked_t (id INTEGER PRIMARY KEY, region TEXT, secret_amount INTEGER);
INSERT INTO masked_t VALUES (1,'EU',10),(2,'EU',20),(3,'EU',30),(4,'EU',40),(5,'EU',50),(6,'EU',60);";
            cmd.ExecuteNonQuery();
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var masked = await RegisterTableAsync(repo, MaskedId, ("id", "integer"), ("region", "varchar"), ("secret_amount", "integer"));
            await repo.CreateConsentAsync(new Consent
            {
                TableId = masked.Table.Id,
                TableIdentifier = MaskedId,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Role,
                RoleName = "Reader",
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = masked.Columns.Select(c => new ConsentColumnRule
                {
                    ColumnName = c.ColumnName,
                    AccessLevel = c.ColumnName == "secret_amount" ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear
                }).ToList(),
                RowFilters = []
            });
        }
    }

    private async Task<JsonDocument> RawAsync(string query)
    {
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        for (var attempt = 0; ; attempt++)
        {
            var response = await client.PostAsJsonAsync("/graphql", new { query });
            var body = await response.Content.ReadAsStringAsync();
            if (body.Contains("does not exist", StringComparison.Ordinal) && attempt < 50)
            {
                await Task.Delay(200);
                continue;
            }

            return JsonDocument.Parse(body);
        }
    }

    private static string ErrorCode(JsonDocument doc) =>
        doc.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString()!;

    [Fact]
    public async Task RestrictedCaller_CountsOnlyTheRowsTheRowFilterAllows()
    {
        using var doc = await RawAsync("{ default_main_air1_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count groups { aggregates { alias intValue } } } }");

        doc.RootElement.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        var data = doc.RootElement.GetProperty("data").GetProperty("default_main_air1_aggregate");
        data.GetProperty("count").GetInt64().ShouldBe(_fixture.ExpectedIds.Count);
        data.GetProperty("groups")[0].GetProperty("aggregates")[0].GetProperty("intValue").GetInt64().ShouldBe(_fixture.ExpectedIds.Count);
        _fixture.ExpectedIds.Count.ShouldBeLessThan(13);
    }

    [Theory]
    [InlineData("aggregates: [{ fn: COUNT_DISTINCT, column: secret_amount, alias: \"d\" }]")]
    [InlineData("aggregates: [{ fn: MIN, column: secret_amount, alias: \"m\" }]")]
    [InlineData("groupBy: [secret_amount], aggregates: [{ fn: COUNT, alias: \"n\" }]")]
    [InlineData("aggregates: [{ fn: COUNT, alias: \"n\" }], where: { secret_amount: { gt: 1 } }")]
    [InlineData("groupBy: [region], aggregates: [{ fn: COUNT, alias: \"n\" }], orderBy: [{ alias: \"n\" }], where: { secret_amount: { eq: 10 } }")]
    public async Task MaskedColumn_CannotBeAggregatedGroupedOrFilteredOn(string arguments)
    {
        using var doc = await RawAsync($"{{ default_main_masked_t_aggregate({arguments}) {{ count }} }}");

        ErrorCode(doc).ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        doc.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()!.ShouldNotContain("secret_amount");
    }

    [Fact]
    public async Task ClearColumns_OfTheSameTable_AreAggregatable()
    {
        using var doc = await RawAsync("{ default_main_masked_t_aggregate(groupBy: [region], aggregates: [{ fn: COUNT, alias: \"n\" }]) { count groups { key { region } rowCount } } }");

        doc.RootElement.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        var group = doc.RootElement.GetProperty("data").GetProperty("default_main_masked_t_aggregate").GetProperty("groups")[0];
        group.GetProperty("key").GetProperty("region").GetString().ShouldBe("EU");
        group.GetProperty("rowCount").GetInt64().ShouldBe(6);
    }
}
