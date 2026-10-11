namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0l: a typed row policy (marker text plus bound values) reaches the dataset query as a literal, because the dataset text is parsed
/// as user SQL by the governed pipeline. The splice itself is removed by WP-X0n.
/// </summary>
public sealed class GovernedDataQueryServiceTypedPolicyTests
{
    private static readonly TableIdentifier Table = new("sales", "main", "orders");

    [Fact]
    public async Task TypedRowPolicy_IsNotSplicedIntoDatasetSql_NeitherAsLiteralNorAsMarker()
    {
        var metadata = new TableMetadata
        {
            Identifier = Table,
            Table = new Table { SourceName = "sales", SchemaName = "main", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "region", DataType = "varchar" }],
            PrimaryKeyColumns = ["id"]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Table, Arg.Any<CancellationToken>()).Returns(metadata);

        var namer = new PolicyParameterNamer();
        var predicate = PolicyPredicate.Create(
            new BinaryExpression(PolicyParameterNamer.Column("region"), BinaryOperator.Equal, namer.Add(new PolicyValue("O'Brien", SqlParameterType.String))),
            namer.Values);
        var policy = new RowPolicyFactory(new PolicyPredicateRenderer()).FromPredicate(predicate, DatabaseDialect.Sqlite);
        var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(policy);
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Table, metadata, Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        GovernedSqlQueryRequest? sent = null;
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => sent = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "r", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1));
        var service = new GovernedDataQueryService(repo, pdp, sql, Substitute.For<IFederatedQueryExecutionService>(), Substitute.For<IColumnMaskingProvider>());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("tenant", "default")], "t"));

        await service.ExecuteQueryAsync(new DatasetQueryRequest(Table, Limit: 10), new RequestContext(user, new TenantId("default")));

        sent.ShouldNotBeNull();
        // WP-X0n: the dataset service no longer splices policy text; the rewriter applies the row policy exactly once.
        sent!.Sql.ShouldNotContain("O'Brien");
        sent.Sql.ShouldNotContain("@__gql");
    }
}
