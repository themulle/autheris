namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-4 (A-8): the MCP tool <c>admin_set_dataset_catalog</c> calls the same service with the principal rebuilt from the session.</summary>
[Trait("Category", "Security")]
public sealed class McpDatasetCatalogAdminToolTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly McpSessionContext Session = new(
        "s1", "sp1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-99999999-500", Roles: ["ClusterAdmin"]);

    private readonly IAccessPlanningService _planning = Substitute.For<IAccessPlanningService>();
    private readonly IDatasetCatalogAdminService _datasets = Substitute.For<IDatasetCatalogAdminService>();

    private Task<McpToolCallResult> Run(string json, IDatasetCatalogAdminService? service = null, McpSessionContext? session = null) =>
        McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetCatalog, json, session ?? Session, _planning, service ?? _datasets).AsTask();

    [Fact]
    public void Tool_IsRegistered_AsAdminTool_Ungrouped_WithClosedSchema()
    {
        var def = McpAdminTools.Definitions().Single(d => d.Name == "admin_set_dataset_catalog");

        McpAdminTools.IsAdminTool("admin_set_dataset_catalog").ShouldBeTrue();
        McpToolGroups.IsKnownBuiltIn("admin_set_dataset_catalog").ShouldBeTrue();
        McpToolGroups.Of("admin_set_dataset_catalog").ShouldBe(McpToolGroup.None);
        def.InputJsonSchema.ShouldContain("\"additionalProperties\": false");
        def.InputJsonSchema.ShouldContain("\"dataset\"");
    }

    [Fact]
    public void SessionOfAClusterAdmin_ResolvesToGlobal_OtherRolesAndReadOnlyAreNoAdminToolUsers()
    {
        CallerScopeResolver.Resolve(McpProtocolHandler.BuildPrincipalFromSession(Session), TenancyMode.Multi).ShouldBe(CallerScope.Global);
        McpAdminTools.CanUseAdminTools(Session with { Roles = ["GovernanceAdmin"] }, authBypassed: false).ShouldBeFalse();
        McpAdminTools.CanUseAdminTools(Session with { IsReadOnly = true }, authBypassed: false).ShouldBeFalse();
    }

    [Fact]
    public async Task ForwardsDatasetTagsAndVisibility_WithTheSessionPrincipal_AndSourceMcp()
    {
        _datasets.ApplyAsync(default, default!, default!, default!, default, default).ReturnsForAnyArgs(new DatasetCatalogResult(
            DatasetCatalogOutcome.Applied, Orders, new DatasetCatalogSettings([new DatasetTag("pii", DatasetTagSource.Admin)], DatasetVisibility.Summary), true));

        var result = await Run("""{"dataset":"sales.dbo.orders","tags":["pii"],"visibility":"Summary"}""");

        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldContain("\"visibility\":\"Summary\"");
        result.ContentJson.ShouldContain("\"tierEffective\":true");
        await _datasets.Received(1).ApplyAsync(
            Arg.Is<ClaimsPrincipal?>(p => p != null && p.FindFirst(ClaimTypes.PrimarySid)!.Value == "S-1-5-21-99999999-500" && p.IsInRole("ClusterAdmin")),
            "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.ReplaceTags!.Single() == "pii" && e.Visibility == "Summary" && !e.Malformed),
            "mcp", Arg.Any<CancellationToken>(), false);
    }

    [Theory]
    [InlineData("""{"dataset":"a.b.c","visibility":2}""")]
    [InlineData("""{"dataset":"a.b.c","visibility":"Full","extra":1}""")]
    [InlineData("""{"dataset":"a.b.c","tags":"pii"}""")]
    [InlineData("""{"tags":["x"]}""")]
    [InlineData("""{"dataset":"a.b.c"}""")]
    [InlineData("""not json""")]
    public async Task MalformedOrIncompleteArguments_AreRejected_BeforeTheService(string json)
    {
        var result = await Run(json);

        result.IsSuccess.ShouldBeFalse();
        _datasets.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DatasetCatalogOutcome.NotFound, "NOT_FOUND")]
    [InlineData(DatasetCatalogOutcome.Forbidden, "FORBIDDEN")]
    [InlineData(DatasetCatalogOutcome.InvalidTag, "INVALID_TAG")]
    [InlineData(DatasetCatalogOutcome.TagLimitExceeded, "TAG_LIMIT_EXCEEDED")]
    [InlineData(DatasetCatalogOutcome.InvalidVisibility, "INVALID_VISIBILITY")]
    [InlineData(DatasetCatalogOutcome.Unavailable, "UNAVAILABLE")]
    public async Task ServiceRefusal_IsAToolError_WithTheStableCode(DatasetCatalogOutcome outcome, string code)
    {
        _datasets.ApplyAsync(default, default!, default!, default!, default, default).ReturnsForAnyArgs(new DatasetCatalogResult(outcome));

        var result = await Run("""{"dataset":"a.b.c","visibility":"Full"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(code);
    }

    [Fact]
    public async Task NoServiceConfigured_IsAToolError_NothingIsAttempted()
    {
        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetCatalog,
            """{"dataset":"a.b.c","visibility":"Full"}""", Session, _planning);

        result.IsSuccess.ShouldBeFalse();
    }
}
