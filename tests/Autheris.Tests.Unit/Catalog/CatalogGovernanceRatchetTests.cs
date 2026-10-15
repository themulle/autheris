using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Catalog;

/// <summary>Issue #94 (SEC-94-11): the ratchet carries an incoming key definition only when it asks to replace it.</summary>
public sealed class CatalogGovernanceRatchetTests
{
    private static TableMetadata Meta(IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> indexes, bool replace) => new()
    {
        Identifier = new TableIdentifier("d", "s", "t"),
        Table = new Table { SourceName = "d", SchemaName = "s", TableName = "t", DisplayName = "t" },
        Columns = [new TableColumn { ColumnName = "a" }, new TableColumn { ColumnName = "b" }],
        PrimaryKeyColumns = pk,
        Indexes = indexes,
        ReplaceKeyDefinition = replace
    };

    [Fact]
    public void Merge_CarriesIncomingDefinitionOnlyWhenFlagged()
    {
        var existing = Meta(["a"], [new TableIndexDefinition("ix_a", ["a"], false)], replace: false);
        var incoming = Meta(["b"], [new TableIndexDefinition("ux_b", ["b"], true)], replace: true);

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.ReplaceKeyDefinition.ShouldBeTrue();
        merged.PrimaryKeyColumns.ShouldBe(["b"]);
        merged.Indexes.Single().Name.ShouldBe("ux_b");
    }

    [Fact]
    public void Merge_UnflaggedIncomingDefinition_KeepsExistingDefinition_AndStaysUnflagged()
    {
        var existing = Meta(["a"], [new TableIndexDefinition("ix_a", ["a"], false)], replace: false);
        var incoming = Meta(["b"], [new TableIndexDefinition("ux_b", ["b"], true)], replace: false);

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.ReplaceKeyDefinition.ShouldBeFalse();
        merged.PrimaryKeyColumns.ShouldBe(["a"]);
        merged.Indexes.Single().Name.ShouldBe("ix_a");
    }

    [Fact]
    public void Merge_FlaggedEmptyIncoming_Clears()
    {
        var existing = Meta(["a"], [], replace: false);
        var merged = CatalogGovernanceRatchet.Merge(Meta([], [], replace: true), existing);
        merged.PrimaryKeyColumns.ShouldBeEmpty();
        merged.ReplaceKeyDefinition.ShouldBeTrue();
    }
}
