namespace Autheris.Tests.Unit.Sql;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Issue #277: the router answers the recursive-CTE constructs from the analysis alone (SEC-277-03, -04, -12), before any table lookup
/// and before any planning or staging.
/// </summary>
public sealed class CrossSourceQueryRouterRecursiveCteTests
{
    private const string Counter =
        "WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 3) SELECT n FROM t";

    private static readonly TenantId Tenant = new("tenant_test");

    private static TableMetadata Table(string domain, string name, string source = "crm") => new()
    {
        Identifier = new TableIdentifier(domain, "public", name),
        Table = new Table
        {
            TableName = name,
            SchemaName = "public",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = source,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static (CrossSourceQueryRouter Router, ITableMetadataRepository Repo) CreateRouter(bool allowRecursive, bool crossSource = false)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<TableIdentifier>();
                return string.Equals(id.TableName, "orders", StringComparison.OrdinalIgnoreCase)
                    ? Table("crm", "orders")
                    : string.Equals(id.TableName, "secret", StringComparison.OrdinalIgnoreCase)
                        ? Table("crm", "secret") with { Table = Table("crm", "secret").Table with { IsActive = false } }   // exists, but the caller may not use it
                    : string.Equals(id.TableName, "shipments", StringComparison.OrdinalIgnoreCase)
                        ? Table("logistics", "shipments", "logistics")
                        : null;
            });
        var webSql = new WebSqlOptions
        {
            Enabled = true,
            DefaultDataSourceName = "crm",
            AllowedDataSources = ["crm", "logistics"],
            AllowRecursiveCte = allowRecursive
        };
        return (new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = crossSource }, webSql), repo);
    }

    private static Task<QueryRoutingDecision> Route(CrossSourceQueryRouter router, string sql) =>
        router.RouteAsync(FastSqlEngine.Default.Analyze(sql.AsMemory()), null, Tenant, isDml: false, CancellationToken.None);

    [Fact]
    public async Task RecursiveCte_SelfReferenceIsNotResolvedAsTable()
    {
        var (router, repo) = CreateRouter(allowRecursive: true);

        var decision = await Route(router,
            "WITH RECURSIVE t(id) AS (SELECT id FROM orders UNION ALL SELECT id + 1 FROM t WHERE id < 3) SELECT id FROM t");

        decision.SourceClass.ShouldBe(QuerySourceClass.HomogeneousSql);
        decision.Tables.Select(t => t.Target.TableName).ShouldBe(["orders"]);
        await repo.DidNotReceive().GetTableMetadataAsync(
            Arg.Is<TableIdentifier>(i => i.TableName.Equals("t", StringComparison.OrdinalIgnoreCase)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TablelessRecursiveCte_IsRoutedHomogeneous_WhenEnabled()
    {
        var (router, repo) = CreateRouter(allowRecursive: true);

        var decision = await Route(router, Counter);

        decision.Tables.ShouldBeEmpty();
        repo.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task NonRecursiveSelfReference_ThrowsConstructNotSupportedBeforeTableLookup()
    {
        var (router, repo) = CreateRouter(allowRecursive: true);

        var ex = await Should.ThrowAsync<SqlCompileNotSupportedException>(
            () => Route(router, "WITH orders AS (SELECT id FROM orders) SELECT id FROM orders"));

        ex.Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
        repo.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_277_04_KeywordlessSelfReference_SameResponseForExistingForbiddenAndUnknownTable()
    {
        var (router, repo) = CreateRouter(allowRecursive: false);

        var existing = await Should.ThrowAsync<SqlCompileNotSupportedException>(
            () => Route(router, "WITH orders AS (SELECT id FROM orders) SELECT id FROM orders"));
        var unknown = await Should.ThrowAsync<SqlCompileNotSupportedException>(
            () => Route(router, "WITH ghost AS (SELECT id FROM ghost) SELECT id FROM ghost"));

        var forbidden = await Should.ThrowAsync<SqlCompileNotSupportedException>(
            () => Route(router, "WITH secret AS (SELECT id FROM secret) SELECT id FROM secret"));

        forbidden.Message.ShouldBe(existing.Message);
        forbidden.Reason.ShouldBe(existing.Reason);
        unknown.GetType().ShouldBe(existing.GetType());
        unknown.Message.ShouldBe(existing.Message);
        unknown.Reason.ShouldBe(existing.Reason);
        existing.Message.ShouldNotContain("orders", Case.Insensitive);
        existing.Message.ShouldNotContain("ghost", Case.Insensitive);
        repo.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("WITH RECURSIVE t(id) AS (SELECT id FROM orders UNION ALL SELECT id + 1 FROM t WHERE id < 3) SELECT id FROM t")]
    [InlineData(Counter)]
    public async Task SEC_277_03_RecursiveCte_WhenDisabled_IsRejectedBeforeAnyTableLookup(string sql)
    {
        var (router, repo) = CreateRouter(allowRecursive: false);

        var ex = await Should.ThrowAsync<SqlCompileNotSupportedException>(() => Route(router, sql));

        ex.Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
        repo.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_277_12_RecursiveCte_CrossSource_IsRejected_EvenWhenEnabled()
    {
        var (router, _) = CreateRouter(allowRecursive: true, crossSource: true);

        var ex = await Should.ThrowAsync<SqlCompileNotSupportedException>(() => Route(router,
            "WITH RECURSIVE t(id) AS (SELECT id FROM orders UNION ALL SELECT s.id FROM shipments s JOIN t ON s.id = t.id) SELECT id FROM t"));

        ex.Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
    }
}
