namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
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
/// Issue #107 (PLAN-GOV-107 WP-C, SEC-RF107-06 f): the key-set cache variant of <see cref="RowFilterChannelParityTests"/>. The same correlated consent
/// filter must yield the same rows in every transport when the global strategy is <c>KeySetCache</c> and the data source carries
/// <c>KeySetCacheAllowed</c>. air1 covers a multi-hop filter and a NULL foreign key (row 13); three more tables cover an empty key set,
/// a DENY consent on the same table and an ALLOW consent OR-combined with a simple filter. Expected sets are computed directly in SQLite.
/// </summary>
public sealed class RowFilterKeySetChannelParityTests : IClassFixture<RowFilterKeySetChannelParityTests.KeySetFixture>
{
    private readonly KeySetFixture _fixture;

    public RowFilterKeySetChannelParityTests(KeySetFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("sql-endpoint")]
    [InlineData("odata")]
    [InlineData("graphql")]
    [InlineData("graphql-tree")]
    [InlineData("mcp-sample-rows")]
    [InlineData("mcp-query-graphql")]
    [InlineData("arrow-export")]
    [InlineData("flight-sql")]
    [InlineData("olap")]
    public async Task KeySetCache_Channel_ReturnsExactlyTheRowsOfTheConsentFilter(string channel)
    {
        var ids = await RowFilterChannelParityTests.ChannelMatrix.Channels[channel](_fixture);

        ids.ShouldBe(_fixture.ExpectedIds, ignoreOrder: true, customMessage: $"channel '{channel}'");
    }

    [Fact]
    public async Task KeySetCache_IsActuallyUsed_AndMatchesTheNullForeignKeyAndMultiHopBaseline()
    {
        long before = KeySetCounters.Substituted;

        var ids = await WebSqlIdsAsync("air1");

        ids.ShouldBe(_fixture.ExpectedIds, ignoreOrder: true);
        ids.ShouldNotContain(13L);   // the NULL foreign key row is not delivered by the EXISTS and must not be by the key set either
        KeySetCounters.Substituted.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task KeySetCache_EmptyKeySet_DeliversNoRows()
    {
        long before = KeySetCounters.Substituted;

        (await WebSqlIdsAsync("air_empty")).ShouldBeEmpty();

        KeySetCounters.Substituted.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task KeySetCache_DenyConsentOnTheSameTable_StillSubtracts()
    {
        var ids = await WebSqlIdsAsync("air_deny");

        ids.ShouldBe(_fixture.Scenario("air_deny"), ignoreOrder: true);
    }

    [Fact]
    public async Task KeySetCache_AllowOrSimpleFilter_UnionsBoth()
    {
        var ids = await WebSqlIdsAsync("air_or");

        ids.ShouldBe(_fixture.Scenario("air_or"), ignoreOrder: true);
        ids.ShouldContain(13L);   // delivered by the simple filter only
    }

    private async Task<IReadOnlySet<long>> WebSqlIdsAsync(string table)
    {
        var r = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql = $"SELECT id FROM default.main.{table}" });
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue(body);
        using var doc = JsonDocument.Parse(body);
        var rows = doc.RootElement.GetProperty("rows");
        var columns = doc.RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetProperty("name").GetString()).ToList();
        int idIndex = columns.IndexOf("id");
        var ids = new HashSet<long>();
        foreach (var row in rows.EnumerateArray())
        {
            var value = row.ValueKind == JsonValueKind.Array ? row[idIndex] : row.GetProperty("id");
            ids.Add(value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt64());
        }

        return ids;
    }

    /// <summary>Counts the sites the gateway served from a key set (hits and misses of the process-wide meter).</summary>
    private static class KeySetCounters
    {
        private static long _substituted;

        static KeySetCounters()
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name is "autheris.rowfilter.keyset.hits" or "autheris.rowfilter.keyset.misses") l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => System.Threading.Interlocked.Add(ref _substituted, value));
            listener.Start();
        }

        public static long Substituted => System.Threading.Volatile.Read(ref _substituted);
    }

    public sealed class KeySetFixture : RowFilterChannelParityTests.Fixture
    {
        private static readonly TableIdentifier Air1Id = new("default", "main", "air1");
        private static readonly TableIdentifier ClientId = new("default", "main", "client");
        private static readonly TableIdentifier CraneId = new("default", "main", "crane");

        private readonly Dictionary<string, IReadOnlySet<long>> _scenarios = new(StringComparer.Ordinal);

        protected override bool UseKeySetCache => true;

        public IReadOnlySet<long> Scenario(string name) => _scenarios[name];

        protected override void SeedExtraData(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE air_empty (id INTEGER PRIMARY KEY, client_id INTEGER, value REAL);
CREATE TABLE air_deny (id INTEGER PRIMARY KEY, client_id INTEGER, value REAL);
CREATE TABLE air_or (id INTEGER PRIMARY KEY, client_id INTEGER, value REAL);
INSERT INTO air_empty SELECT * FROM air1;
INSERT INTO air_deny SELECT * FROM air1;
INSERT INTO air_or SELECT * FROM air1;";
            cmd.ExecuteNonQuery();

            const string David = "EXISTS (SELECT 1 FROM client c JOIN crane k ON k.serial_number = c.crane_serial_number WHERE c.client_id = a.client_id AND k.is_delivered IS NULL)";
            _scenarios["air_deny"] = Ids(connection, $"SELECT a.id FROM air_deny a WHERE {David} AND NOT (a.value > 4.0)");
            _scenarios["air_or"] = Ids(connection, $"SELECT a.id FROM air_or a WHERE {David} OR a.id = 13");
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            async Task<TableMetadata> Register(string name) => await RegisterTableAsync(repo, new TableIdentifier("default", "main", name),
                ("id", "integer"), ("client_id", "integer"), ("value", "real"));

            ConsentRowFilter David(string predicate = "{\"crane.is_delivered\": null}") => new()
            {
                FilterType = RowFilterType.SubqueryCorrelated,
                DependentTable = ClientId,
                DependentTableAlias = "client",
                PrimaryKeyColumn = "client_id",
                ForeignKeyColumn = "client_id",
                AdditionalHops = [new SubqueryJoinHop { Table = CraneId, TableAlias = "crane", LeftJoinColumn = "crane.serial_number", RightJoinColumn = "client.crane_serial_number" }],
                SubqueryFilterPredicateJson = predicate
            };

            var empty = await Register("air_empty");
            await repo.CreateConsentAsync(Consent(empty, new TableIdentifier("default", "main", "air_empty"), [David("{\"crane.is_delivered\": 99}")]));

            var deny = await Register("air_deny");
            var denyId = new TableIdentifier("default", "main", "air_deny");
            await repo.CreateConsentAsync(Consent(deny, denyId, [David()]));
            await repo.CreateConsentAsync(Consent(deny, denyId, [new ConsentRowFilter { ColumnName = "value", Operator = "GT", ValueType = "number", ValueJson = "4.0" }], ConsentEffect.Deny));

            var or = await Register("air_or");
            var orId = new TableIdentifier("default", "main", "air_or");
            await repo.CreateConsentAsync(Consent(or, orId, [David()]));
            await repo.CreateConsentAsync(Consent(or, orId, [new ConsentRowFilter { ColumnName = "id", Operator = "EQ", ValueType = "number", ValueJson = "13" }]));
        }

        private static IReadOnlySet<long> Ids(SqliteConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var ids = new HashSet<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return ids;
        }
    }
}
