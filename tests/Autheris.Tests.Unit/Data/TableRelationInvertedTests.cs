namespace Autheris.Tests.Unit.Data;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>TREE-01: a relation stored in the reverse direction of a navigation is inverted before the cross-source resolver reads it.</summary>
public sealed class TableRelationInvertedTests
{
    private static TableRelation Relation(RelationCardinality cardinality) => new()
    {
        ParentTableId = Guid.NewGuid(),
        ChildTableId = Guid.NewGuid(),
        ParentTableIdentifier = new TableIdentifier("crm", "public", "customers"),
        ChildTableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
        RelationName = "orders",
        JoinKeysParent = ["id"],
        JoinKeysChild = ["customer_id"],
        Cardinality = cardinality,
        AllowCrossDataSource = true
    };

    [Theory]
    [InlineData(RelationCardinality.OneToMany, RelationCardinality.ManyToOne)]
    [InlineData(RelationCardinality.ManyToOne, RelationCardinality.OneToMany)]
    [InlineData(RelationCardinality.OneToOne, RelationCardinality.OneToOne)]
    public void Inverted_SwapsSidesAndCardinality(RelationCardinality stored, RelationCardinality expected)
    {
        var relation = Relation(stored);

        var inverted = relation.Inverted();

        inverted.ParentTableIdentifier.ShouldBe(relation.ChildTableIdentifier);
        inverted.ChildTableIdentifier.ShouldBe(relation.ParentTableIdentifier);
        inverted.ParentTableId.ShouldBe(relation.ChildTableId);
        inverted.ChildTableId.ShouldBe(relation.ParentTableId);
        inverted.JoinKeysParent.ShouldBe(["customer_id"]);
        inverted.JoinKeysChild.ShouldBe(["id"]);
        inverted.Cardinality.ShouldBe(expected);
        inverted.RelationName.ShouldBe("orders");
        inverted.AllowCrossDataSource.ShouldBeTrue();
        inverted.Inverted().ParentTableIdentifier.ShouldBe(relation.ParentTableIdentifier);
    }
}
