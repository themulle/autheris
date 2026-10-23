using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA-06 follow-up ordering: a client error that does not depend on the row policy (a missing required filter column) is reported with
/// its stable code before the row-policy support check, and the policy refusal stays fixed and free of policy details.
/// </summary>
public sealed class TreeRequiredFilterBeforePolicyDenialTests : IDisposable
{
    private readonly SecuredTreeHarness _h;

    public TreeRequiredFilterBeforePolicyDenialTests() =>
        // An empty catalog makes the secured-source builder deny the typed subquery policy (the case the builder cannot serve).
        _h = new SecuredTreeHarness(catalogTables: []);

    public void Dispose() => _h.Dispose();

    private GovernedTreeQueryService CreateService()
    {
        var plain = SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId);
        var meta = plain with { Table = plain.Table with { RequiredFilterColumns = ["amount"] } };
        var access = SecuredTreeHarness.Resolved(
            meta, SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()), "t1");
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(access);
        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [SecuredTreeHarness.DataSource] = new() { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" }
                }
            }
        };
        return new GovernedTreeQueryService(
            resolver, Substitute.For<ISqlConnectionFactory>(), Substitute.For<IAuditLogRepository>(),
            Substitute.For<IColumnMaskingProvider>(), Options.Create(options), securedSourceBuilder: _h.Builder);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_MissingRequiredFilter_IsReportedBeforeTheRowPolicyDenial()
    {
        var service = CreateService();
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"]) { Limit = 10 };

        var ex = await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(
            () => service.ExecuteAsync(new ClaimsPrincipal(), root, null, Guid.NewGuid().ToString("N")));

        ex.Code.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_SatisfiedRequiredFilter_StillDeniesTheUnservablePolicyWithTheFixedCode()
    {
        var service = CreateService();
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"])
        {
            Limit = 10,
            Where = new TreeComparison("amount", TreeFilterOperator.Eq, 5L)
        };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(
            () => service.ExecuteAsync(new ClaimsPrincipal(), root, null, Guid.NewGuid().ToString("N")));

        ex.Message.ShouldBe("The row policy of this table is not supported in tree queries.");
    }
}
