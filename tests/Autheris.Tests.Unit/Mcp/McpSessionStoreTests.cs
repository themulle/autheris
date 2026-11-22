namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Mcp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class McpSessionStoreTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Touch_WhenSessionRemovedConcurrently_DoesNotResurrectSession()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var session = store.CreateSession("sp-agent", "tenant-alpha", "S-1-5-21-1001");

        var touched = store.Touch(session.SessionId, () => store.RemoveSession(session.SessionId));

        touched.ShouldBeNull();
        store.GetSession(session.SessionId).ShouldBeNull();
        store.Count.ShouldBe(0);
    }
}
