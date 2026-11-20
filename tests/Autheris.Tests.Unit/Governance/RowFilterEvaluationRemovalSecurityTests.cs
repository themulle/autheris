using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

[Trait("Category", "Security")]
public sealed class RowFilterEvaluationRemovalSecurityTests
{
    [Fact]
    public void RemovedMcpAdminTools_AreNotRegisteredOrRecognized()
    {
        McpAdminTools.IsAdminTool("admin_plan_row_filter_evaluation").ShouldBeFalse();
        McpAdminTools.IsAdminTool("admin_apply_row_filter_evaluation").ShouldBeFalse();
        McpAdminTools.Definitions().Select(t => t.Name).ShouldNotContain("admin_plan_row_filter_evaluation");
        McpAdminTools.Definitions().Select(t => t.Name).ShouldNotContain("admin_apply_row_filter_evaluation");
    }

    [Fact]
    public void RemovedRowFilterOptionsAndAuditEvents_DoNotExist()
    {
        typeof(GatewayOptions).GetProperty("RowFilters").ShouldBeNull();
        typeof(DataSourceConnectionOptions).GetProperty("KeySetCacheAllowed").ShouldBeNull();
        typeof(AuditEventTypes).GetField("RowFilterEvaluationChanged").ShouldBeNull();
        typeof(AuditEventTypes).GetField("TableRowFilterStrategyChanged").ShouldBeNull();
    }
}
