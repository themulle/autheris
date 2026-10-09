namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 5: the David case as an access profile. Every channel returns exactly the rows the filter
/// allows, although David holds two unrestricted consents on air1 (the second one used to lift any consent filter),
/// and an object in scope that no filter covers (crane) is denied.
/// </summary>
public sealed class VirtualFilterChannelParityTests : IClassFixture<VirtualFilterChannelParityTests.VirtualFilterFixture>
{
    public sealed class VirtualFilterFixture : RowFilterChannelParityTests.Fixture
    {
        protected override bool UseVirtualFilters => true;
    }

    private readonly VirtualFilterFixture _fixture;

    public VirtualFilterChannelParityTests(VirtualFilterFixture fixture) => _fixture = fixture;

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
    public async Task Channel_ReturnsExactlyTheRowsOfTheVirtualFilter(string channel)
    {
        var ids = await RowFilterChannelParityTests.ChannelMatrix.Channels[channel](_fixture);

        ids.ShouldBe(_fixture.ExpectedIds, ignoreOrder: true, customMessage: $"channel '{channel}'");
    }

    [Fact]
    public async Task EffectiveFilters_ExplainsTheDecisionForDavid()
    {
        var auditor = _fixture.Client();
        auditor.DefaultRequestHeaders.Remove("X-Test-Roles");
        auditor.DefaultRequestHeaders.Add("X-Test-Roles", "SecurityAuditor");

        var response = await auditor.GetAsync($"/api/v1/governance/effective-filters?user={RowFilterChannelParityTests.Fixture.DavidSid}&table=default.main.air1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("decision").GetString().ShouldBe("allow");
        body.RootElement.GetProperty("sql").GetString()!.ShouldStartWith("EXISTS (SELECT 1 FROM \"client\" AS \"client\"");
        body.RootElement.GetProperty("applied_filters")[0].GetString().ShouldBe("nicht_ausgelieferte_krane");
    }

    [Fact]
    public async Task UncoveredObject_IsDenied()
    {
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT serial_number FROM crane" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

/// <summary>Virtual filters, phase 7: the David filter as a sql definition gives the same rows in every channel.</summary>
public sealed class SqlVirtualFilterChannelParityTests : IClassFixture<SqlVirtualFilterChannelParityTests.SqlFixture>
{
    public sealed class SqlFixture : RowFilterChannelParityTests.Fixture
    {
        protected override bool UseVirtualFilters => true;
        protected override bool UseSqlDefinition => true;
    }

    private readonly SqlFixture _fixture;

    public SqlVirtualFilterChannelParityTests(SqlFixture fixture) => _fixture = fixture;

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
    public async Task Channel_ReturnsExactlyTheRowsOfTheSqlFilter(string channel)
    {
        var ids = await RowFilterChannelParityTests.ChannelMatrix.Channels[channel](_fixture);

        ids.ShouldBe(_fixture.ExpectedIds, ignoreOrder: true, customMessage: $"channel '{channel}'");
    }
}
