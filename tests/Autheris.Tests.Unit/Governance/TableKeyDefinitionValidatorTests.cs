using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #94 (D-7, SEC-94-07, SEC-94-11): every rule of the declared key/index validator.</summary>
public sealed class TableKeyDefinitionValidatorTests
{
    private static TableMetadata Meta(
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        params string[] columns)
    {
        if (columns.Length == 0) columns = ["a", "b", "c", "d"];
        return new TableMetadata
        {
            Identifier = new TableIdentifier("d", "s", "t"),
            Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList(),
            PrimaryKeyColumns = pk ?? [],
            Indexes = indexes ?? []
        };
    }

    [Fact]
    public void EmptyDefinition_IsValid_AndStaysEmpty()
    {
        var result = TableKeyDefinitionValidator.Validate(Meta());
        result.PrimaryKeyColumns.ShouldBeEmpty();
        result.Indexes.ShouldBeEmpty();
    }

    [Fact]
    public void CompositeKeyAndIndexes_AreNormalizedToCatalogSpelling()
    {
        var result = TableKeyDefinitionValidator.Validate(Meta(
            pk: [" B ", "a"],
            indexes: [new TableIndexDefinition("ix_x", ["C", "d"], true)]));

        result.PrimaryKeyColumns.ShouldBe(["b", "a"]);
        result.Indexes.Single().Columns.ShouldBe(["c", "d"]);
        result.Indexes.Single().IsUnique.ShouldBeTrue();
    }

    [Fact]
    public void KeyColumnLimit_32Accepted_33Rejected()
    {
        var cols = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
        TableKeyDefinitionValidator.Validate(Meta(pk: cols.Take(32).ToList(), columns: cols))
            .PrimaryKeyColumns.Count.ShouldBe(32);
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(pk: cols.Take(33).ToList(), columns: cols)));
    }

    [Fact]
    public void IndexColumnLimit_32Accepted_33Rejected()
    {
        var cols = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
        TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", cols.Take(32).ToList(), false)], columns: cols));
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", cols.Take(33).ToList(), false)], columns: cols)));
    }

    [Fact]
    public void IndexCountLimit_64Accepted_65Rejected()
    {
        List<TableIndexDefinition> Many(int n) => Enumerable.Range(0, n).Select(i => new TableIndexDefinition($"ix_{i}", ["a"], false)).ToList();
        TableKeyDefinitionValidator.Validate(Meta(indexes: Many(64))).Indexes.Count.ShouldBe(64);
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(indexes: Many(65))));
    }

    [Fact]
    public void OversizedArrays_RejectedBeforeAllocation()
    {
        // A lazily enumerated list that throws when iterated proves the cap is checked from Count only.
        var hugePk = new ThrowingList<string>(1_000_000);
        var hugeIndexes = new ThrowingList<TableIndexDefinition>(1_000_000);
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(pk: hugePk)));
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(indexes: hugeIndexes)));
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", new ThrowingList<string>(1_000_000), false)])));
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("ix\n")]
    [InlineData("ix\u0000")]
    [InlineData("ix`; drop table x")]
    public void IndexName_WithMarkupOrControlCharacters_Rejected(string name)
    {
        var ex = Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new(name, ["a"], false)])));
        ex.Message.ShouldNotContain("<");
        ex.Message.ShouldNotContain("\n");
        ex.Message.ShouldNotContain("\u0000");
    }

    [Fact]
    public void IndexName_OversizedRejected_AndNotEchoedInFull()
    {
        var name = "i" + new string('x', 5000);
        var ex = Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new(name, ["a"], false)])));
        ex.Message.Length.ShouldBeLessThan(400);
    }

    [Fact]
    public void IndexName_128Characters_Accepted_129Rejected()
    {
        TableKeyDefinitionValidator.Validate(Meta(indexes: [new("i" + new string('x', 127), ["a"], false)]));
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("i" + new string('x', 128), ["a"], false)])));
    }

    [Fact]
    public void DuplicateIndexNames_CaseInsensitive_Rejected()
    {
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(
            indexes: [new("ix_a", ["a"], false), new("IX_A", ["b"], true)])));
    }

    [Fact]
    public void DuplicateColumnInKey_Rejected()
    {
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(pk: ["a", "A"])));
    }

    [Fact]
    public void DuplicateColumnInIndex_Rejected()
    {
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", ["a", "a"], false)])));
    }

    [Fact]
    public void UnknownColumn_InKeyOrIndex_Rejected()
    {
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(pk: ["zzz"])));
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", ["a", "zzz"], false)])));
    }

    [Fact]
    public void EmptyOrBlankColumnName_Rejected()
    {
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(pk: ["a", " "])));
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", [""], false)])));
    }

    [Fact]
    public void IndexWithoutColumns_Rejected()
    {
        Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(indexes: [new("ix", [], false)])));
    }

    [Fact]
    public void UnknownColumnName_WithMarkup_IsNotEchoedRaw()
    {
        var ex = Should.Throw<TableDefinitionException>(() =>
            TableKeyDefinitionValidator.Validate(Meta(pk: ["<img src=x onerror=1>"])));
        ex.Message.ShouldNotContain("<");
    }

    [Fact]
    public void UniqueIndexIdenticalToPrimaryKey_IsAllowed()
    {
        var result = TableKeyDefinitionValidator.Validate(Meta(pk: ["a"], indexes: [new("ux_a", ["a"], true)]));
        result.Indexes.Count.ShouldBe(1);
    }

    private sealed class ThrowingList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("must not be enumerated");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("must not be enumerated");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
