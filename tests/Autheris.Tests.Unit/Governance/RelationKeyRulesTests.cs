using System.Reflection;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #151 WP-151-1: relation key rules and the removal of single-key accessors.</summary>
public sealed class RelationKeyRulesTests
{
    [Fact]
    public void Validate_ReturnsNull_ForEqualNonEmptyKeys()
    {
        RelationKeyRules.Validate(["a", "b"], ["x", "y"]).ShouldBeNull();
    }

    [Fact]
    public void Validate_ReturnsCountMismatch()
    {
        RelationKeyRules.Validate(["a", "b"], ["x"]).ShouldBe(RelationKeyViolation.CountMismatch);
    }

    [Fact]
    public void Validate_ReturnsMissingColumns_ForEmptyLists()
    {
        RelationKeyRules.Validate([], []).ShouldBe(RelationKeyViolation.MissingColumns);
        RelationKeyRules.Validate(["a"], []).ShouldBe(RelationKeyViolation.MissingColumns);
        RelationKeyRules.Validate([], ["a"]).ShouldBe(RelationKeyViolation.MissingColumns);
        RelationKeyRules.Validate(null!, ["a"]).ShouldBe(RelationKeyViolation.MissingColumns);
    }

    [Fact]
    public void Validate_ReturnsEmptyName_ForBlankColumn()
    {
        RelationKeyRules.Validate(["a", " "], ["x", "y"]).ShouldBe(RelationKeyViolation.EmptyName);
        RelationKeyRules.Validate(["a", "b"], ["x", ""]).ShouldBe(RelationKeyViolation.EmptyName);
    }

    [Fact]
    public void Validate_ReturnsDuplicateColumn_CaseInsensitive()
    {
        RelationKeyRules.Validate(["a", "A"], ["x", "y"]).ShouldBe(RelationKeyViolation.DuplicateColumn);
        RelationKeyRules.Validate(["a", "b"], ["x", "X"]).ShouldBe(RelationKeyViolation.DuplicateColumn);
    }

    [Fact]
    public void Validate_AllowsSameNameOnBothSides()
    {
        RelationKeyRules.Validate(["a", "b"], ["a", "b"]).ShouldBeNull();
    }

    [Fact]
    public void Validate_ReturnsTooWide_Above16()
    {
        RelationKeyRules.MaxKeyColumns.ShouldBe(16);
        var ok = Enumerable.Range(0, 16).Select(i => "c" + i).ToArray();
        RelationKeyRules.Validate(ok, ok).ShouldBeNull();
        var wide = Enumerable.Range(0, 17).Select(i => "c" + i).ToArray();
        RelationKeyRules.Validate(wide, wide).ShouldBe(RelationKeyViolation.TooWide);
    }

    [Fact]
    public void InvalidRelationException_CarriesViolationAndMessage()
    {
        var ex = new InvalidRelationException(RelationKeyViolation.TooWide, "boom");
        ex.Violation.ShouldBe(RelationKeyViolation.TooWide);
        ex.Message.ShouldBe("boom");
        new InvalidRelationException(null, "x").Violation.ShouldBeNull();
    }

    [Fact]
    public void TableRelation_HasNoSingleKeyAccessors()
    {
        typeof(TableRelation).GetProperty("JoinKeyParent").ShouldBeNull();
        typeof(TableRelation).GetProperty("JoinKeyChild").ShouldBeNull();
        typeof(TableRelation).GetProperty(nameof(TableRelation.JoinKeysParent), BindingFlags.Public | BindingFlags.Instance).ShouldNotBeNull();
    }

    [Fact]
    public void TableRelation_DefaultKeys_AreEmpty()
    {
        var r = new TableRelation();
        r.JoinKeysParent.ShouldBeEmpty();
        r.JoinKeysChild.ShouldBeEmpty();
        r.IsComposite.ShouldBeFalse();
    }
}
