using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Connectors;

/// <summary>#301 / SEC-PG-07: a zero page given to the SQL table read executor stays zero; it is not rounded up to one row.</summary>
public sealed class SqlTableReadExecutorZeroLimitTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_LimitZero_PlansAnUnsatisfiableOneRowRead_AndReturnsNoRows()
    {
        var id = new TableIdentifier("shop", "main", "orders");
        var meta = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "shop", SchemaName = "main", TableName = "orders", DataSourceType = DataSourceType.Sql },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var decision = TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        GovernedSqlQueryRequest? captured = null;
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1, false));
        var executor = new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governed);
        var context = new DataSourceExecutionContext(
            "shop", meta, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1")], "Test")), decision,
            new Dictionary<string, object?>(), ["id"], Limit: 0, Tenant: new TenantId("t1"));

        var rows = await executor.ExecuteAsync(context);

        rows.ShouldBeEmpty();
        captured.ShouldNotBeNull();
        captured.Sql.ShouldContain("1 = 0");
        captured.Sql.ShouldEndWith("LIMIT 1");
        captured.Sql.ShouldNotContain("LIMIT 0");
    }
}
