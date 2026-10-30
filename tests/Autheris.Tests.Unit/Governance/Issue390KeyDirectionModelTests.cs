using System.Reflection;
using Autheris.Application.Data.Services;
using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #390 (WP-390-2): model, validator, reader and ratchet rules of the declared key and index column directions.</summary>
public sealed class Issue390KeyDirectionModelTests
{
    private const KeyColumnDirection Asc = KeyColumnDirection.Ascending;
    private const KeyColumnDirection Desc = KeyColumnDirection.Descending;
    private static readonly TableIdentifier Id = new("d", "s", "t");

    private static TableMetadata Meta(
        IReadOnlyList<string>? pk = null,
        IReadOnlyList<KeyColumnDirection>? directions = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        bool replace = false) => new()
    {
        Identifier = Id,
        Columns = new[] { "a", "b", "c" }.Select(c => new TableColumn { ColumnName = c }).ToList(),
        PrimaryKeyColumns = pk ?? [],
        PrimaryKeyDirections = directions ?? [],
        Indexes = indexes ?? [],
        ReplaceKeyDefinition = replace
    };

    [Fact]
    public void Validator_DirectionCountMismatch_IsInvalid()
    {
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(["a", "b"], [Desc])));
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(["a"], [Asc, Desc])));
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(null, [Desc])), "directions without a key");
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(["a"], [(KeyColumnDirection)7])), "only defined enum values");
        Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(indexes: [new TableIndexDefinition("ix", ["a", "b"], true, [Desc])])));
    }

    [Fact]
    public void Validator_MismatchMessage_DoesNotEchoInput()
    {
        var ex = Should.Throw<TableDefinitionException>(() => TableKeyDefinitionValidator.Validate(Meta(["a", "b"], [Desc])));

        ex.Message.ShouldNotContain("Desc", Case.Insensitive);
    }

    [Fact]
    public void Validator_AllAscending_NormalizesToEmpty_AndMixedKeepsPerColumnEntries()
    {
        var ascending = TableKeyDefinitionValidator.Validate(Meta(["a", "b"], [Asc, Asc], [new TableIndexDefinition("ix", ["a", "b"], false, [Asc, Asc])]));
        ascending.PrimaryKeyDirections.ShouldBeEmpty();
        ascending.Indexes.Single().Directions.ShouldBeNull();

        var mixed = TableKeyDefinitionValidator.Validate(Meta([" B ", "a"], [Asc, Desc], [new TableIndexDefinition("ix", ["C", "a"], true, [Desc, Asc])]));
        mixed.PrimaryKeyColumns.ShouldBe(["b", "a"]);
        mixed.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        mixed.Indexes.Single().Directions.ShouldBe([Desc, Asc]);
    }

    [Theory]
    [InlineData("ASC", true, Asc)]
    [InlineData("asc", true, Asc)]
    [InlineData("DeSc", true, Desc)]
    [InlineData("DESC;", false, Asc)]
    [InlineData("descending", false, Asc)]
    [InlineData("", false, Asc)]
    [InlineData(null, false, Asc)]
    public void KeyDirections_TryParse_AcceptsOnlyTheClosedSet(string? value, bool ok, KeyColumnDirection expected)
    {
        KeyDirections.TryParse(value, out var parsed).ShouldBe(ok);
        if (ok)
        {
            parsed.ShouldBe(expected);
        }
    }

    [Fact]
    public void KeyDirections_IsDescending_OutOfRangeOrEmptyReadsAscending()
    {
        KeyDirections.IsDescending(null, 0).ShouldBeFalse();
        KeyDirections.IsDescending([], 3).ShouldBeFalse();
        KeyDirections.IsDescending([Asc, Desc], 1).ShouldBeTrue();
        KeyDirections.IsDescending([Asc, Desc], 2).ShouldBeFalse();
        KeyDirections.ToWire([Asc, Desc]).ShouldBe(["ASC", "DESC"]);
        KeyDirections.ToWire([Asc, Asc]).ShouldBeEmpty();
    }

    [Fact]
    public void IndexDirections_SerializeAsClosedStrings_AndAreOmittedWhenNull()
    {
        System.Text.Json.JsonSerializer.Serialize(new TableIndexDefinition("ix", ["a", "b"], true, [Asc, Desc]))
            .ShouldContain("\"Directions\":[\"ASC\",\"DESC\"]");
        System.Text.Json.JsonSerializer.Serialize(new TableIndexDefinition("ix", ["a"], true))
            .ShouldNotContain("Directions");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    [InlineData(-1, false, false)]
    [InlineData(255, false, false)]
    public void I390_T_06_Reader_InvalidIsDescendingValue_FailsClosed(int value, bool ok, bool descending)
    {
        CatalogKeyDefinitionStore.TryReadDescending((long)value, out var d).ShouldBe(ok);
        d.ShouldBe(descending);
        CatalogKeyDefinitionStore.TryReadDescending(value != 0, out var b).ShouldBeTrue();
        b.ShouldBe(value != 0);
        CatalogKeyDefinitionStore.TryReadDescending("1", out _).ShouldBeFalse("a non-numeric value is never read");
        CatalogKeyDefinitionStore.TryReadDescending(1.0, out _).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_06_Assemble_BuildsDirectionsFromRows_AndCanonicalizes()
    {
        var a = new TableColumn { ColumnName = "a" };
        var b = new TableColumn { ColumnName = "b" };
        IReadOnlyList<TableColumn> columns = [a, b];

        var mixed = CatalogKeyDefinitionStore.Assemble(Id, columns,
            [new CatalogKeyDefinitionStore.PrimaryKeyRow(1, a.Id.ToString(), false), new CatalogKeyDefinitionStore.PrimaryKeyRow(2, b.Id.ToString(), true)], []);
        mixed.PrimaryKeyDirections.ShouldBe([Asc, Desc]);

        var ascending = CatalogKeyDefinitionStore.Assemble(Id, columns,
            [new CatalogKeyDefinitionStore.PrimaryKeyRow(1, a.Id.ToString()), new CatalogKeyDefinitionStore.PrimaryKeyRow(2, b.Id.ToString())], []);
        ascending.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Restrict_HiddenKeyColumn_DropsKeyAndDirections()
    {
        var meta = Meta(["a", "b"], [Asc, Desc], [new TableIndexDefinition("ix", ["a", "c"], true, [Desc, Asc])]);

        var partial = TableKeyVisibility.Restrict(meta, [meta.Columns[0], meta.Columns[2]]);
        partial.PrimaryKeyColumns.ShouldBeEmpty();
        partial.PrimaryKeyDirections.ShouldBeEmpty("no direction survives for a partial key");
        partial.Indexes.Single().Directions.ShouldBe([Desc, Asc], "the index is fully visible");

        var noIndex = TableKeyVisibility.Restrict(meta, [meta.Columns[0], meta.Columns[1]]);
        noIndex.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        noIndex.Indexes.ShouldBeEmpty("an index touching a hidden column is omitted as a whole, with its directions");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Ratchet_IncomingWithoutDeclaration_KeepsStoredDirections()
    {
        var existing = Meta(["a", "b"], [Asc, Desc]);
        var incoming = Meta(replace: false);

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        merged.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_13_Ratchet_Merge_DirectionsNeverCrossSides()
    {
        var existing = Meta(["a", "b"], [Asc, Desc]);

        var replacedByAscending = CatalogGovernanceRatchet.Merge(Meta(["b", "c"], null, replace: true), existing);
        replacedByAscending.PrimaryKeyColumns.ShouldBe(["b", "c"]);
        replacedByAscending.PrimaryKeyDirections.ShouldBeEmpty("the incoming side declared no direction; the stored [ASC, DESC] must not be combined with the incoming columns");

        var replacedByDescending = CatalogGovernanceRatchet.Merge(Meta(["c"], [Desc], replace: true), Meta(["a", "b"]));
        replacedByDescending.PrimaryKeyColumns.ShouldBe(["c"]);
        replacedByDescending.PrimaryKeyDirections.ShouldBe([Desc]);

        var kept = CatalogGovernanceRatchet.Merge(Meta(["c"], [Desc], replace: false), existing);
        kept.PrimaryKeyColumns.ShouldBe(["a", "b"]);
        kept.PrimaryKeyDirections.ShouldBe([Asc, Desc], "no replace intent: columns and directions both stay with the stored side");
    }
}
