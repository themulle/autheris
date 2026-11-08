namespace Autheris.Tests.Unit.Sql;

using Autheris.Application.Sql.Interfaces;
using Shouldly;
using Xunit;

/// <summary>
/// SEC_51_12: only WebSQL-style client SQL is metered by the statement gate; REST, MCP, Arrow, SqlEndpoints, AsyncJob and
/// OLAP reads are priced by their own transports and must never be charged by the WebSQL gate (federated path included).
/// </summary>
public sealed class SqlTransportsCostMeteringTests
{
    [Theory]
    [InlineData("WebSql", true)]
    [InlineData("Trino", true)]
    [InlineData("trino", true)]
    [InlineData("Parquet", true)]
    [InlineData("Future", true)]
    [InlineData("  ", true)]
    [InlineData("rest", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Rest", false)]
    [InlineData("Mcp", false)]
    [InlineData("ArrowExport", false)]
    [InlineData("SqlEndpoints", false)]
    [InlineData("AsyncJob", false)]
    [InlineData("Olap", false)]
    [Trait("Category", "Security")]
    public void IsCostMetered_OnlyClientSqlTransports_SEC_51_12(string? transport, bool expected)
        => SqlTransports.IsCostMetered(transport).ShouldBe(expected);
}
