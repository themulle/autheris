namespace Autheris.Tests.Unit.Mcp;

using System.Security;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #107 (SEC-RF107-14b, c): the two MCP admin tools for row filter evaluation settings.</summary>
[Trait("Category", "Security")]
public sealed class McpRowFilterEvaluationToolsTests
{
    private const string AdminSid = "S-1-5-21-99999999-500";
    private static readonly McpSessionContext Session = new(
        "s1", "sp1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: AdminSid, Roles: ["ClusterAdmin"]);

    private readonly IAccessPlanningService _planning = Substitute.For<IAccessPlanningService>();

    [Fact]
    public void Definitions_ContainBothTools_AndTheyAreAdminTools_WithClosedSchemas()
    {
        var defs = McpAdminTools.Definitions().ToDictionary(d => d.Name);

        defs.ShouldContainKey(McpAdminTools.PlanRowFilterEvaluation);
        defs.ShouldContainKey(McpAdminTools.ApplyRowFilterEvaluation);
        McpAdminTools.IsAdminTool("admin_plan_row_filter_evaluation").ShouldBeTrue();
        McpAdminTools.IsAdminTool("admin_apply_row_filter_evaluation").ShouldBeTrue();
        defs[McpAdminTools.PlanRowFilterEvaluation].InputJsonSchema.ShouldContain("\"additionalProperties\": false");
        defs[McpAdminTools.ApplyRowFilterEvaluation].InputJsonSchema.ShouldContain("\"additionalProperties\": false");
    }

    [Fact]
    public void ToolsAreNotAvailableToNonClusterAdminOrReadOnlySessions()
    {
        McpAdminTools.CanUseAdminTools(Session with { Roles = ["GovernanceAdmin"] }, authBypassed: false).ShouldBeFalse();
        McpAdminTools.CanUseAdminTools(Session with { IsReadOnly = true }, authBypassed: false).ShouldBeFalse();
    }

    [Fact]
    public async Task Plan_ForwardsTheRequestWithAdminAndTenant()
    {
        var expected = new AdminPlanRowFilterEvaluationResult(
            "plan_1", "object", null, "sales.dbo.orders", new(null, null, null), new("KeySetCache", 60, 10), [], DateTimeOffset.UtcNow.AddMinutes(5));
        _planning.PlanRowFilterEvaluationAsync(Arg.Any<AdminPlanRowFilterEvaluationRequest>(), AdminSid, "tenant-a", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.PlanRowFilterEvaluation,
            """{"datasetId":"sales.dbo.orders","strategy":"KeySetCache","durationSeconds":60,"maxKeys":10,"reason":"tune"}""", Session, _planning);

        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldContain("plan_1");
        await _planning.Received(1).PlanRowFilterEvaluationAsync(
            Arg.Is<AdminPlanRowFilterEvaluationRequest>(r => r.DatasetId == "sales.dbo.orders" && r.Strategy == "KeySetCache" && r.DurationSeconds == 60 && r.MaxKeys == 10 && r.RowFilterId == null),
            AdminSid, "tenant-a", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"datasetId":"a.b.c","strategy":1}""")]
    [InlineData("""{"datasetId":"a.b.c","strategy":true}""")]
    [InlineData("""{"datasetId":"a.b.c","strategy":"KeySetCache","unknownProperty":1}""")]
    [InlineData("""{"datasetId":"a.b.c","durationSeconds":"60"}""")]
    [InlineData("""{"datasetId":"a.b.c","durationSeconds":1.5}""")]
    public async Task Plan_NumericEnumsUnknownPropertiesAndBadNumbers_AreRejected_BeforeTheService_SEC_RF107_14b(string json)
    {
        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.PlanRowFilterEvaluation, json, Session, _planning);

        result.IsSuccess.ShouldBeFalse();
        _planning.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_ServiceRejection_IsReturnedAsToolError()
    {
        _planning.PlanRowFilterEvaluationAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<Task<AdminPlanRowFilterEvaluationResult>>(_ => throw new RowFilterEvaluationValidationException("maxKeys exceeds the configured maximum of 2000."));

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.PlanRowFilterEvaluation, """{"datasetId":"a.b.c","maxKeys":99999}""", Session, _planning);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("maxKeys");
    }

    [Fact]
    public async Task Apply_ForwardsToTheRowFilterApplyPath_NotTheAccessApplyPath()
    {
        _planning.ApplyRowFilterEvaluationAsync(Arg.Any<AdminApplyAccessRequest>(), AdminSid, "tenant-a", Arg.Any<CancellationToken>())
            .Returns(new AdminApplyRowFilterEvaluationResult(true, "plan_1", "rule", "a.b.c", 4));

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.ApplyRowFilterEvaluation, """{"planId":"plan_1","confirmationToken":"tok"}""", Session, _planning);

        result.IsSuccess.ShouldBeTrue();
        await _planning.Received(1).ApplyRowFilterEvaluationAsync(
            Arg.Is<AdminApplyAccessRequest>(r => r.PlanId == "plan_1" && r.ConfirmationToken == "tok"), AdminSid, "tenant-a", Arg.Any<CancellationToken>());
        await _planning.DidNotReceiveWithAnyArgs().ApplyAccessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Apply_KindMismatchSecurityException_IsReturnedAsToolError()
    {
        _planning.ApplyRowFilterEvaluationAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<Task<AdminApplyRowFilterEvaluationResult>>(_ => throw new SecurityException("The plan is not of the kind this operation applies."));

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.ApplyRowFilterEvaluation, """{"planId":"plan_1","confirmationToken":"tok"}""", Session, _planning);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("kind");
    }

    [Fact]
    public async Task Apply_UnknownProperty_IsRejected()
    {
        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.ApplyRowFilterEvaluation, """{"planId":"p","confirmationToken":"t","force":true}""", Session, _planning);

        result.IsSuccess.ShouldBeFalse();
        _planning.ReceivedCalls().ShouldBeEmpty();
    }
}
