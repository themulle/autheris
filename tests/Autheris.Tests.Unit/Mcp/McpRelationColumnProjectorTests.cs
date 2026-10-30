namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Issue #392: join column pairs of describe_dataset relations are disclosed all-or-nothing.</summary>
public sealed class McpRelationColumnProjectorTests
{
    private static readonly TableIdentifier Orders = new("crm", "dbo", "orders");
    private static readonly TableIdentifier Customers = new("crm", "dbo", "customers");

    private static McpDatasetColumn Col(string name) => new(name, "int", null, false, false);

    private static readonly IReadOnlyList<McpDatasetColumn> OwnColumns = [Col("customer_id"), Col("tenant_id"), Col("amount")];

    private static GraphQlRelationMapping Relation(params string[][] pairs) =>
        new("customer", Customers, false, pairs.Select(p => p[0]).ToList(), pairs.Select(p => p[1]).ToList());

    private static Task<IReadOnlyList<McpGraphQlRelation>> Build(
        GraphQlRelationMapping relation,
        Func<TableIdentifier, CancellationToken, ValueTask<IReadOnlySet<string>?>> lookup,
        IReadOnlyList<McpDatasetColumn>? own = null) =>
        new McpRelationColumnProjector(lookup)
            .BuildAsync([relation], new HashSet<string> { "crm.dbo.customers" }, t => $"{t.Domain}.{t.Schema}.{t.TableName}", own ?? OwnColumns, CancellationToken.None)
            .AsTask();

    private static Func<TableIdentifier, CancellationToken, ValueTask<IReadOnlySet<string>?>> Visible(params string[] columns) =>
        (_, _) => ValueTask.FromResult<IReadOnlySet<string>?>(columns.ToHashSet(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task Describe_RelationColumns_AllVisible_ListsPairsInOrder()
    {
        var result = await Build(Relation(["tenant_id", "t_id"], ["customer_id", "id"]), Visible("id", "t_id", "name"));

        result.ShouldHaveSingleItem().Columns.ShouldBe([new McpRelationColumnPair("tenant_id", "t_id"), new McpRelationColumnPair("customer_id", "id")]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_OwnColumnHidden_AllPairsNull()
    {
        // I392-T-07: one hidden own column drops every pair, never a subset.
        var result = await Build(Relation(["customer_id", "id"], ["secret", "t_id"]), Visible("id", "t_id"));

        var relation = result.ShouldHaveSingleItem();
        relation.Field.ShouldBe("customer");
        relation.Columns.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_TargetColumnHidden_AllPairsNull()
    {
        var result = await Build(Relation(["customer_id", "id"], ["tenant_id", "t_id"]), Visible("id"));

        result.ShouldHaveSingleItem().Columns.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_TargetColumnDenied_AllPairsNull()
    {
        // The visible-column set of the target excludes a Deny column, so the pair containing it is not disclosed.
        var result = await Build(Relation(["customer_id", "id"], ["tenant_id", "denied_col"]), Visible("id", "name"));

        result.ShouldHaveSingleItem().Columns.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_TargetSummaryShape_Null()
    {
        // A summary (or otherwise not fully visible) target yields no column set.
        var result = await Build(Relation(["customer_id", "id"]), (_, _) => ValueTask.FromResult<IReadOnlySet<string>?>(null));

        result.ShouldHaveSingleItem().Columns.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_LookupThrows_Null()
    {
        var result = await Build(Relation(["customer_id", "id"]), (_, _) => throw new InvalidOperationException("boom"));

        result.ShouldHaveSingleItem().Columns.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Describe_RelationColumns_HiddenTarget_RelationNotListed_NoLookup()
    {
        var lookups = 0;
        var projector = new McpRelationColumnProjector((_, _) => { lookups++; return ValueTask.FromResult<IReadOnlySet<string>?>(new HashSet<string> { "id" }); });

        var result = await projector.BuildAsync([Relation(["customer_id", "id"])], new HashSet<string>(), t => t.TableName, OwnColumns, CancellationToken.None);

        result.ShouldBeEmpty();
        lookups.ShouldBe(0);
    }

    [Fact]
    public async Task Describe_RelationColumns_OneLookupPerDistinctTarget()
    {
        var lookups = 0;
        var projector = new McpRelationColumnProjector((_, _) => { lookups++; return ValueTask.FromResult<IReadOnlySet<string>?>(new HashSet<string> { "id" }); });

        await projector.BuildAsync(
            [Relation(["customer_id", "id"]), Relation(["tenant_id", "id"])],
            new HashSet<string> { "crm.dbo.customers" }, t => $"{t.Domain}.{t.Schema}.{t.TableName}", OwnColumns, CancellationToken.None);

        lookups.ShouldBe(1);
    }

    [Fact]
    public async Task Describe_RelationColumns_NoColumnsOnMapping_Null()
    {
        var result = await Build(new GraphQlRelationMapping("customer", Customers, false), Visible("id"));

        result.ShouldHaveSingleItem().Columns.ShouldBeNull();
    }
}
