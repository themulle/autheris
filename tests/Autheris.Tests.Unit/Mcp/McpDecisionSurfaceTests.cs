namespace Autheris.Tests.Unit.Mcp;

using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>MCP tools only submit, withdraw and list access requests; no tool decides, grants, renews or revokes.</summary>
[Trait("Category", "Security")]
public sealed class McpDecisionSurfaceTests
{
    private static readonly Regex DecisionVerb = new(
        "approve|reject|decide|decision|revoke|renew|self_?grant|grant_access|apply_grant",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void No_decide_tool()
    {
        var names = McpDatasetTools.Definitions().Select(d => d.Name).ToList();

        names.ShouldContain(McpDatasetTools.RequestAccess);
        names.Where(n => DecisionVerb.IsMatch(n)).ShouldBeEmpty();
    }
}
