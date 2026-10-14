namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-03: the shared authorization and audit stages.</summary>
[Trait("Category", "Security")]
public sealed class GovernedQueryStagesTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata Metadata() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "int" } }
    };

    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public async Task Authorize_NonCanonicalClusterAdminClaim_Throws()
    {
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true));
        var user = User(new Claim(ClaimTypes.Role, "ClusterAdmin"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"));

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            GovernedQueryStages.AuthorizeTableAsync(pdp, Orders, Metadata(), user, new TenantId("default"), null, null, CancellationToken.None));
        ex.Message.ShouldContain("not canonical");
    }

    [Fact]
    public async Task Authorize_DeniedDecision_ThrowsWithReasons()
    {
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Denied(Orders, "no consent"));

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            GovernedQueryStages.AuthorizeTableAsync(pdp, Orders, Metadata(), User(), new TenantId("default"), null, null, CancellationToken.None));
        ex.Message.ShouldContain("no consent");
    }

    [Fact]
    public void BuildEntry_SetsDecisionEventAndDetails()
    {
        var decision = TableAccessDecision.Denied(Orders, "why");
        var details = GovernedQueryStages.DecisionDetails(decision, path: "graphql_tree");

        var entry = GovernedQueryStages.BuildEntry("TABLE_QUERY", new TenantId("default"), new Sid("S-1-5-21-1"), Orders.ToString(), false, details, "trace");

        entry.Decision.ShouldBe("DENY");
        entry.EventType.ShouldBe("TABLE_QUERY");
        entry.TraceId.ShouldBe("trace");
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.GetProperty("is_allowed").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("path").GetString().ShouldBe("graphql_tree");
    }

    [Fact]
    public async Task Write_RequiredTier_PropagatesFailure()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("down"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            GovernedQueryStages.WriteAsync(audit, new AuditLogEntry(), AuditTier.Required, CancellationToken.None));
    }

    [Fact]
    public async Task Write_BestEffortTier_SwallowsFailure()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("down"));

        await GovernedQueryStages.WriteAsync(audit, new AuditLogEntry(), AuditTier.BestEffort, CancellationToken.None);
    }
}
