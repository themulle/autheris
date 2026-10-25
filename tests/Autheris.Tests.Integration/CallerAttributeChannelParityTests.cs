namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// REQ-GOV-129 (PLAN-GOV-129 tests 27, 29, 30): one consent with a caller attribute (<c>client_id IN client_ids</c>, resolved from an attribute
/// table by the Windows SID of the caller) must give every person exactly their rows in every channel, an application-only caller exactly its
/// service set, and a caller without an attribute (or with a failing lookup) nothing, with the same shape of response. Real consents, real
/// SQLite data. Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~CallerAttributeChannelParityTests"
/// </summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeChannelParityTests : IClassFixture<CallerAttributeChannelParityTests.AttributeFixture>
{
    private const string PersonA = "S-1-5-21-LWE-A";
    private const string PersonB = "S-1-5-21-LWE-B";
    private const string PersonC = "S-1-5-21-LWE-NOATTR";
    private const string AppId = "aaaaaaaa-0000-0000-0000-0000000000a1";

    private readonly AttributeFixture _fixture;

    public CallerAttributeChannelParityTests(AttributeFixture fixture) => _fixture = fixture;

    public sealed class AttributeFixture : RowFilterChannelParityTests.Fixture
    {
        private static readonly TableIdentifier Air1Id = new("default", "main", "air1");
        private static readonly TableIdentifier ClientId = new("default", "main", "client");
        private static readonly TableIdentifier BrokenId = new("default", "main", "air_broken");
        private static readonly TenantId TenantValue = new(Tenant);

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:CallerAttributes:Enabled", "true");
            builder.UseSetting("Gateway:CallerAttributes:Claims:AllowedSchemes:0", "TestAuth");
            builder.UseSetting("Gateway:DataSources:Connections:default:CallerAttributeLookupAllowed", "true");
            builder.UseSetting("Gateway:DataSources:Connections:default:CallerAttributeDedicatedLogin", "true");
        }

        protected override void SeedExtraData(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
CREATE TABLE person_attributes (person_sid TEXT, client_id INTEGER, tenant_id TEXT);
INSERT INTO person_attributes VALUES ('{PersonA}', 1, '{Tenant}'), ('{PersonA}', 3, '{Tenant}'), ('{PersonA}', 2, 'another_tenant'), ('{PersonB}', 5, '{Tenant}');
CREATE TABLE air_broken (id INTEGER PRIMARY KEY, client_id INTEGER, value REAL);
INSERT INTO air_broken SELECT * FROM air1;";
            cmd.ExecuteNonQuery();
        }

        protected override IReadOnlyCollection<string> RequiredGraphQlFields =>
            [.. base.RequiredGraphQlFields, "default_main_air_broken"];

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            var air1 = (await repo.GetTableMetadataAsync(Air1Id))!;
            var client = (await repo.GetTableMetadataAsync(ClientId))!;
            var broken = await RegisterTableAsync(repo, BrokenId, ("id", "integer"), ("client_id", "integer"), ("value", "real"));

            ConsentRowFilter Attribute(string name) => new() { ColumnName = "client_id", Operator = "IN", ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = name };
            Consent ReaderConsent(TableMetadata meta, TableIdentifier id, params ConsentRowFilter[] filters) => new()
            {
                TableId = meta.Table.Id, TableIdentifier = id, TenantId = TenantValue, Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Role, RoleName = "Reader",
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
                RowFilters = filters
            };

            await repo.CreateConsentAsync(ReaderConsent(air1, Air1Id, Attribute("client_ids")));
            await repo.CreateConsentAsync(ReaderConsent(client, ClientId));
            await repo.CreateConsentAsync(ReaderConsent(broken, BrokenId, Attribute("broken_ids")));

            var store = Services.GetRequiredService<ICallerAttributeRepository>();
            async Task Activate(string name, IReadOnlyDictionary<string, string> config)
            {
                var definition = new CallerAttributeDefinition(
                    Guid.NewGuid(), TenantValue, name, CallerAttributeType.Whole, CallerAttributeCardinality.List, 50, 60,
                    new CallerAttributeProviderBinding("attribute-table", config), null, FilterApprovalStatus.PendingApproval);
                (await store.SubmitDefinitionAsync(definition, "seed-author", DateTimeOffset.UtcNow)).ShouldBe(CallerAttributeChangeResult.Applied);
                (await store.ApproveDefinitionAsync(TenantValue, name, "seed-approver", definition.ComputeDefinitionHash(), DateTimeOffset.UtcNow)).ShouldBe(CallerAttributeChangeResult.Applied);
            }

            Dictionary<string, string> Table(string table) => new()
            {
                ["dataSource"] = "default", ["table"] = table, ["subjectKeyKind"] = "WindowsSid", ["subjectKeyColumn"] = "person_sid",
                ["valueColumn"] = "client_id", ["tenantColumn"] = "tenant_id"
            };

            await Activate("client_ids", Table("person_attributes"));
            await Activate("broken_ids", Table("no_such_table"));

            var set = new ServiceAttributeSet(Guid.Empty, TenantValue, AppId, "client_ids", ["2"], FilterApprovalStatus.PendingApproval);
            (await store.SubmitServiceSetAsync(TenantValue, AppId, "client_ids", ["2"], "seed-author", DateTimeOffset.UtcNow)).ShouldBe(CallerAttributeChangeResult.Applied);
            (await store.ApproveServiceSetAsync(TenantValue, AppId, "client_ids", "seed-approver", set.ComputeValuesHash(), DateTimeOffset.UtcNow)).ShouldBe(CallerAttributeChangeResult.Applied);
        }
    }

    public static IEnumerable<object[]> Channels() => new[]
    {
        "websql", "trino", "sql-endpoint", "odata", "graphql", "graphql-tree", "mcp-sample-rows", "mcp-query-graphql", "arrow-export", "flight-sql", "olap"
    }.Select(c => new object[] { c });

    private async Task<IReadOnlySet<long>> Ids(string channel, string sid, string? appId = null)
    {
        _fixture.CallerSid = sid;
        _fixture.ExtraHeaders.Clear();
        if (appId != null)
        {
            _fixture.ExtraHeaders["X-Test-AppId"] = appId;
            _fixture.ExtraHeaders["X-Test-IdTyp"] = "app";
        }

        try
        {
            return await RowFilterChannelParityTests.ChannelMatrix.Channels[channel](_fixture);
        }
        finally
        {
            _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;
            _fixture.ExtraHeaders.Clear();
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Channel_ReturnsExactlyTheRowsOfEachPerson(string channel)
    {
        (await Ids(channel, PersonA)).ShouldBe(new HashSet<long> { 1, 2, 5, 6 }, ignoreOrder: true, customMessage: $"person A, channel '{channel}'");
        (await Ids(channel, PersonB)).ShouldBe(new HashSet<long> { 9, 10 }, ignoreOrder: true, customMessage: $"person B, channel '{channel}'");
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Channel_ACallerWithoutTheAttribute_ReceivesNothing(string channel)
    {
        (await Ids(channel, PersonC)).ShouldBeEmpty($"channel '{channel}'");
    }

    [Theory]
    [InlineData("websql")]
    [InlineData("odata")]
    [InlineData("graphql")]
    [InlineData("trino")]
    public async Task ApplicationOnlyCaller_GetsItsServiceSet_AndAnApplicationWithoutOneGetsNothing(string channel)
    {
        (await Ids(channel, "S-1-5-21-LWE-APP", AppId)).ShouldBe(new HashSet<long> { 3, 4 }, ignoreOrder: true, customMessage: $"channel '{channel}'");
        (await Ids(channel, "S-1-5-21-LWE-APP2", "bbbbbbbb-0000-0000-0000-0000000000b2")).ShouldBeEmpty($"channel '{channel}'");
    }

    [Fact]
    public async Task ST_129_10_AFailedLookup_AndAMissingAttribute_AreIndistinguishableToTheCaller()
    {
        async Task<(HttpStatusCode Status, string Properties, int Rows)> Query(string table)
        {
            _fixture.CallerSid = PersonC;
            var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql = $"SELECT id FROM default.main.{table}" });
            _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return (response.StatusCode, string.Join(',', doc.RootElement.EnumerateObject().Select(p => p.Name).Where(n => n is "rows" or "columns").Order()), doc.RootElement.GetProperty("rows").GetArrayLength());
        }

        var missing = await Query("air1");        // person C has no row in the attribute table
        var failed = await Query("air_broken");   // the attribute table of broken_ids does not exist: the provider fails

        failed.ShouldBe(missing);
        missing.Status.ShouldBe(HttpStatusCode.OK);
        missing.Rows.ShouldBe(0);
    }

    [Fact]
    public async Task ST_129_14_AClientParameterNamedLikeASlot_IsRefused_AndChangesNothing()
    {
        _fixture.CallerSid = PersonA;
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new
        {
            sql = "SELECT id FROM default.main.air1 WHERE id > @__gql_caller_client_ids__0",
            parameters = new Dictionary<string, object> { ["__gql_caller_client_ids__0"] = 0 }
        });
        _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task ST_129_Forged_InputsHaveNoEffect_OnTheResolvedValue()
    {
        // Header, query and body spelled like an attribute never reach the resolver: person B keeps their own rows.
        _fixture.CallerSid = PersonB;
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("X-Caller-Client-Ids", "1,2,3");
        client.DefaultRequestHeaders.Add("client_ids", "1");
        var response = await client.PostAsJsonAsync("/api/v1/sql?client_ids=1&caller.client_ids=1", new { sql = "SELECT id FROM default.main.air1 WHERE 1 = 1", client_ids = new[] { 1, 2, 3 } });
        _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateObject().First().Value.GetInt64()).Order().ShouldBe([9L, 10L]);
    }

    [Fact]
    public async Task ST_129_21_AggregatesAndExists_ReturnTheShapeOfAnEmptyPermittedSet_ForACallerWithoutTheAttribute()
    {
        async Task<string> Scalar(string sid, string sql)
        {
            _fixture.CallerSid = sid;
            var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql });
            _fixture.CallerSid = RowFilterChannelParityTests.Fixture.DavidSid;
            response.StatusCode.ShouldBe(HttpStatusCode.OK, sql);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("rows")[0].EnumerateObject().First().Value.ToString();
        }

        (await Scalar(PersonC, "SELECT COUNT(*) FROM default.main.air1")).ShouldBe("0");
        (await Scalar(PersonA, "SELECT COUNT(*) FROM default.main.air1")).ShouldBe("4");
        (await Scalar(PersonC, "SELECT COUNT(*) FROM default.main.air1 WHERE EXISTS (SELECT 1 FROM default.main.air1 a2)")).ShouldBe("0");
    }
}
