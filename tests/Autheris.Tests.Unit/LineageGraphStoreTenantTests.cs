namespace Autheris.Tests.Unit;

using System.Linq;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Lineage;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LineageGraphStoreTenantTests
{
    private static LineageNode Node(string id, string? tenant)
        => new(id, id, LineageNodeType.Table, [], TenantId: tenant);

    [Fact]
    public void UpdateGraph_SecondTenant_DoesNotWipeFirstTenant()
    {
        var store = new LineageGraphStore();
        store.UpdateGraph([Node("a1", "tenant-a")]);

        store.UpdateGraph([Node("b1", "tenant-b")]);

        store.ContainsNode("a1").ShouldBeTrue();
        store.ContainsNode("b1").ShouldBeTrue();
    }

    [Fact]
    public void UpdateGraph_SameTenant_ReplacesItsNodes()
    {
        var store = new LineageGraphStore();
        store.UpdateGraph([Node("a1", "tenant-a"), Node("a2", "tenant-a")]);

        store.UpdateGraph([Node("a3", "tenant-a")]);

        store.GetAllNodes().Select(n => n.Id).ShouldBe(["a3"]);
    }

    [Fact]
    public void UpdateGraph_Untenanted_ReplacesWholeUntenantedGraph()
    {
        var store = new LineageGraphStore();
        store.UpdateGraph([Node("x1", null)]);

        store.UpdateGraph([Node("x2", null)]);

        store.ContainsNode("x1").ShouldBeFalse();
        store.ContainsNode("x2").ShouldBeTrue();
    }
}
