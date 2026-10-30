using Autheris.Application.Data.Services;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;

namespace Autheris.Tests.Unit.DataCatalog;

/// <summary>Issue #404 WP-404-2: the pure resolver of physical non-unique indexes (D-4 mapping and de-duplication, D-5 states).</summary>
public sealed class PhysicalNonUniqueIndexTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;

    private static readonly TableIdentifier Id = new("sales", "dbo", "orders");

    private static TableMetadata Declared(string[]? pk = null, params TableIndexDefinition[] indexes) => new()
    {
        Identifier = Id,
        Columns = [new TableColumn { ColumnName = "Id" }, new TableColumn { ColumnName = "Ts" }, new TableColumn { ColumnName = "Code" }],
        PrimaryKeyColumns = pk ?? [],
        Indexes = indexes
    };

    private static PhysicalKeySnapshot Known(TargetSqlDialect dialect, params PhysicalNonUniqueIndex[] indexes) =>
        new(PhysicalKeySnapshotState.Known, [new PhysicalKey(true, true, ["Id"], [A])], dialect, new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Known, indexes));

    private static PhysicalNonUniqueIndex Idx(string[] columns, params KeyColumnDirection[] directions) =>
        new(columns, directions.Length == 0 ? columns.Select(_ => A).ToArray() : directions);

    [Fact]
    public void Reconciler_IgnoresNonUniqueRows_403BehaviourUnchanged()
    {
        var incoming = Declared(["Id"]) with { ReplaceKeyDefinition = true };
        var withNonUnique = Known(TargetSqlDialect.SqlServer, Idx(["Ts"]));
        var without = new PhysicalKeySnapshot(PhysicalKeySnapshotState.Known, withNonUnique.Keys, TargetSqlDialect.SqlServer);

        var a = PhysicalKeyDirectionReconciler.Reconcile(incoming, null, withNonUnique);
        var b = PhysicalKeyDirectionReconciler.Reconcile(incoming, null, without);

        a.Metadata.PrimaryKeyColumns.ShouldBe(b.Metadata.PrimaryKeyColumns);
        a.Metadata.PrimaryKeyDirections.ShouldBe(b.Metadata.PrimaryKeyDirections);
        a.Metadata.Indexes.ShouldBe(b.Metadata.Indexes);
        a.Findings.ShouldBe(b.Findings);
        a.PhysicalApplied.ShouldBe(b.PhysicalApplied);
    }

    [Fact]
    public void Resolve_Known_MapsColumnsToCatalogSpelling_WithDirections()
    {
        var result = PhysicalNonUniqueIndexResolver.Resolve(Declared(), Known(TargetSqlDialect.SqlServer, Idx(["ts", "CODE"], A, D)));

        var index = result!.ShouldHaveSingleItem();
        index.Columns.ShouldBe(["Ts", "Code"], "SQL Server ignores case, the catalog spelling is kept");
        index.Directions.ShouldBe([A, D]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-04")]
    public void UnmappedColumn_DropsIndex_AndPostgreSqlMapsCaseSensitive()
    {
        var sqlServer = PhysicalNonUniqueIndexResolver.Resolve(Declared(), Known(TargetSqlDialect.SqlServer, Idx(["Ts", "nope"]), Idx(["Code"])));
        sqlServer!.ShouldHaveSingleItem().Columns.ShouldBe(["Code"], "an index with an unmapped column is dropped whole");

        var postgres = PhysicalNonUniqueIndexResolver.Resolve(Declared(), Known(TargetSqlDialect.PostgreSql, Idx(["ts"]), Idx(["Ts"])));
        postgres!.ShouldHaveSingleItem().Columns.ShouldBe(["Ts"]);
    }

    [Fact]
    public void Dedup_EqualToDeclaredOrPk_Dropped_PrefixKept()
    {
        var declared = Declared(["Id", "Ts"], new TableIndexDefinition("ix_code", ["Code"], false));
        var result = PhysicalNonUniqueIndexResolver.Resolve(declared, Known(
            TargetSqlDialect.SqlServer,
            Idx(["Id", "Ts"]),            // equals the primary key
            Idx(["Code"]),                // equals a declared index
            Idx(["Id"]),                  // a prefix of the key is kept
            Idx(["Ts"]), Idx(["ts"]),     // the second is a duplicate of the first
            Idx(["Ts"], D)));             // other direction, kept

        result!.Select(i => string.Join(",", i.Columns) + "|" + string.Join(",", i.Directions)).ToArray()
            .ShouldBe(["Id|", "Ts|", "Ts|Descending"]);
    }

    [Fact]
    public void Resolve_States_UnavailableKeeps_UnsupportedClears()
    {
        PhysicalNonUniqueIndexResolver.Resolve(Declared(), PhysicalKeySnapshot.Unavailable).ShouldBeNull();
        PhysicalNonUniqueIndexResolver.Resolve(Declared(), PhysicalKeySnapshot.Unsupported)!.ShouldBeEmpty();
        var nonUniqueOnlyUnavailable = new PhysicalKeySnapshot(
            PhysicalKeySnapshotState.Known, [], TargetSqlDialect.SqlServer, new PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState.Unavailable));
        PhysicalNonUniqueIndexResolver.Resolve(Declared(), nonUniqueOnlyUnavailable).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-09")]
    public void PhysicalIndexes_NeverAffectReadOrderingKeysetPolicyMasking()
    {
        var plain = Declared(["Id"]);
        var withPhysical = plain with { PhysicalIndexes = [new PhysicalIndexDefinition(["Code"], []), new PhysicalIndexDefinition(["Ts"], [D])] };
        var decision = new TableAccessDecision(
            Id, true,
            new Dictionary<string, ColumnAccessLevel> { ["Id"] = ColumnAccessLevel.Clear, ["Ts"] = ColumnAccessLevel.Clear, ["Code"] = ColumnAccessLevel.Clear },
            null, []);

        var a = DefaultReadOrdering.Resolve(plain, decision);
        var b = DefaultReadOrdering.Resolve(withPhysical, decision);

        b.Kind.ShouldBe(a.Kind);
        b.Terms.ShouldBe(a.Terms);
        DefaultReadOrdering.TryKey(withPhysical, _ => true, out var key, out _).ShouldBeTrue();
        key.ShouldBe(["Id"], "a physical index is never a key candidate");
        withPhysical.Indexes.ShouldBeEmpty();
    }
}
