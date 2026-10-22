namespace Autheris.Tests.Unit.Mcp;

using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-05: a brief schema never claims GRANTED by default.
/// </summary>
public sealed class Mcp05BriefSchemaTests
{
    [Fact]
    public void MCP_05_BriefSchema_DoesNotDefaultToGranted()
    {
        var schema = new McpTableBriefSchema("sales.public.orders", "sales", null, [], [], []);

        schema.AccessStatus.ShouldBeNull();
    }
}
