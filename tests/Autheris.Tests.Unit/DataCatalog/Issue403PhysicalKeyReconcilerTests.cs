using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;

namespace Autheris.Tests.Unit.DataCatalog;

/// <summary>I403 WP-403-2: the pure reconciler (D-4 matching, D-5 precedence).</summary>
public sealed class Issue403PhysicalKeyReconcilerTests
{
    private const KeyColumnDirection A = KeyColumnDirection.Ascending;
    private const KeyColumnDirection D = KeyColumnDirection.Descending;

    private static readonly TableIdentifier Id = new("sales", "dbo", "orders");

    private static TableMetadata Incoming(string[] pk, KeyColumnDirection[]? pkDirs = null, params TableIndexDefinition[] indexes) => new()
    {
        Identifier = Id,
        PrimaryKeyColumns = pk,
        PrimaryKeyDirections = pkDirs ?? [],
        Indexes = indexes,
        ReplaceKeyDefinition = pk.Length > 0 || indexes.Length > 0
    };

    private static TableMetadata Stored(string[] pk, KeyColumnDirection[]? pkDirs = null, params TableIndexDefinition[] indexes) =>
        Incoming(pk, pkDirs, indexes) with { ReplaceKeyDefinition = false };

    private static PhysicalKey Key(bool pk, bool clustered, string[] columns, params KeyColumnDirection[] directions) =>
        new(pk, clustered, columns, directions.Length == 0 ? columns.Select(_ => A).ToArray() : directions);

    private static PhysicalKeySnapshot Known(TargetSqlDialect dialect, params PhysicalKey[] keys) =>
        new(PhysicalKeySnapshotState.Known, keys, dialect);

    private static PhysicalKeySnapshot Known(params PhysicalKey[] keys) => Known(TargetSqlDialect.SqlServer, keys);

    private static TableIndexDefinition Unique(string name, string[] columns, KeyColumnDirection[]? directions = null) => new(name, columns, true, directions);

    [Fact]
    public void Match_PrimaryKey_AppliesPhysicalDirections()
    {
        // I403-T-10
        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a", "b"]), null, Known(Key(true, true, ["a", "b"], A, D)));

        r.PhysicalApplied.ShouldBeTrue();
        r.Metadata.PrimaryKeyDirections.ShouldBe([A, D]);
        r.WithoutPhysical.PrimaryKeyDirections.ShouldBeEmpty();
        r.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void NoPhysicalPk_ClusteredUnique_Applies()
    {
        // I403-T-11
        var snapshot = Known(Key(false, false, ["a"], A), Key(false, true, ["a"], D));

        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a"]), null, snapshot);

        r.Metadata.PrimaryKeyDirections.ShouldBe([D]);
    }

    [Fact]
    public void NoPhysicalPk_SingleUnique_Applies()
    {
        // I403-T-12
        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a", "b"]), null, Known(Key(false, false, ["a", "b"], D, D)));

        r.Metadata.PrimaryKeyDirections.ShouldBe([D, D]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-04")]
    public void PhysicalPkDiffersInColumns_Mismatch_NoDirection()
    {
        // I403-T-13: no fallback to a unique index even when one matches
        var snapshot = Known(Key(true, true, ["x"], D), Key(false, false, ["a"], D));

        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a"]), null, snapshot);

        r.PhysicalApplied.ShouldBeFalse();
        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-04")]
    public void PhysicalPkDiffersInOrder_Mismatch_NoDirection()
    {
        // I403-T-14
        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a", "b"]), null, Known(Key(true, true, ["b", "a"], D, D)));

        r.PhysicalApplied.ShouldBeFalse();
        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-04")]
    public void TwoUniqueCandidatesDifferentDirections_Ambiguous()
    {
        // I403-T-15
        var snapshot = Known(Key(false, false, ["a"], A), Key(false, false, ["a"], D));

        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a"]), null, snapshot);

        r.PhysicalApplied.ShouldBeFalse();
        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-04")]
    public void PartialOverlapOrReorder_NoDirectionApplied()
    {
        // I403-T-S03: subset, superset, reorder, and the case variant per dialect
        var cases = new (string[] Incoming, PhysicalKey Physical, TargetSqlDialect Dialect)[]
        {
            (["a", "b"], Key(true, true, ["a"], D), TargetSqlDialect.SqlServer),
            (["a"], Key(true, true, ["a", "b"], D, D), TargetSqlDialect.SqlServer),
            (["a", "b"], Key(true, true, ["b", "a"], D, D), TargetSqlDialect.SqlServer),
            (["A"], Key(true, true, ["a"], D), TargetSqlDialect.PostgreSql)
        };
        foreach (var (incoming, physical, dialect) in cases)
        {
            var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(incoming), null, Known(dialect, physical));
            r.PhysicalApplied.ShouldBeFalse();
            r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
        }

        // SQL Server compares case-insensitively and applies
        PhysicalKeyDirectionReconciler.Reconcile(Incoming(["A"]), null, Known(TargetSqlDialect.SqlServer, Key(true, true, ["a"], D)))
            .Metadata.PrimaryKeyDirections.ShouldBe([D]);
    }

    [Fact]
    public void AssetDeclaredDirections_Win_DriftReported()
    {
        // I403-T-16
        var r = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a"], [A]), null, Known(Key(true, true, ["a"], D)));
        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
        r.Findings.ShouldBe([PhysicalKeyDirectionReconciler.DriftCode]);

        var declared = PhysicalKeyDirectionReconciler.Reconcile(Incoming(["a", "b"], [D, A]), null, Known(Key(true, true, ["a", "b"], A, A)));
        declared.Metadata.PrimaryKeyDirections.ShouldBe([D, A]);
        declared.PhysicalApplied.ShouldBeFalse();
        declared.Findings.ShouldBe([PhysicalKeyDirectionReconciler.DriftCode]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-06")]
    public void StoredNonEmptyDirections_Kept_ConflictReported_NoCrossSide()
    {
        // I403-T-17: stored (a DESC, b ASC) vs physical (a ASC, b DESC): the stored list is kept whole, physical is never mixed in
        var r = PhysicalKeyDirectionReconciler.Reconcile(
            Incoming(["a", "b"]), Stored(["a", "b"], [D, A]), Known(Key(true, true, ["a", "b"], A, D)));

        r.PhysicalApplied.ShouldBeFalse();
        r.Metadata.PrimaryKeyDirections.ShouldBe([D, A]);
        r.Findings.ShouldBe([PhysicalKeyDirectionReconciler.ConflictCode]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-06")]
    public void NoIncomingKey_StoredUntouched_DriftOnly()
    {
        // I403-T-18
        var incoming = Incoming([]);
        var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, Stored(["a"], [A]) with { PrimaryKeyDirections = [] }, Known(Key(true, true, ["a"], D)));

        r.Metadata.ShouldBeSameAs(incoming);
        r.PhysicalApplied.ShouldBeFalse();
        r.Findings.ShouldBe([PhysicalKeyDirectionReconciler.DriftCode]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-01")]
    public void Unavailable_Or_Unsupported_NoChange()
    {
        // I403-T-19
        foreach (var snapshot in new[] { PhysicalKeySnapshot.Unavailable, PhysicalKeySnapshot.Unsupported, default })
        {
            var incoming = Incoming(["a"]);
            var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, null, snapshot);

            r.PhysicalApplied.ShouldBeFalse();
            r.Metadata.ShouldBeSameAs(incoming);
            r.Findings.ShouldBeEmpty();
        }
    }

    [Fact]
    public void UniqueIndexDirections_ReconciledLikePk()
    {
        // I403-T-20: a unique index is backed by a physical unique index and also by the physical primary key of the same columns
        var snapshot = Known(Key(true, true, ["id"]), Key(false, false, ["code", "ts"], A, D));
        var incoming = Incoming(["id"], null, Unique("uq_1", ["code", "ts"]), new TableIndexDefinition("ix", ["x"], false));

        var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, null, snapshot);

        r.PhysicalApplied.ShouldBeTrue();
        r.Metadata.Indexes[0].Directions.ShouldBe([A, D]);
        r.Metadata.Indexes[1].Directions.ShouldBeNull();
        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
        r.WithoutPhysical.Indexes[0].Directions.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-05")]
    public void AssetWithoutDirections_SameColumns_StoredDescRetained_PkAndUnique_FlagPath()
    {
        // I403-T-S04 (reconciler half): the incoming side (post Apply) carries no directions; Unsupported and Unavailable physical results
        var stored = Stored(["a", "b"], [D, A], Unique("uq_1", ["c"], [D]));
        foreach (var snapshot in new[] { PhysicalKeySnapshot.Unsupported, PhysicalKeySnapshot.Unavailable })
        {
            var incoming = Incoming(["a", "b"], null, Unique("uq_9", ["c"]));

            var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, stored, snapshot);

            r.Metadata.PrimaryKeyDirections.ShouldBe([D, A]);
            r.Metadata.Indexes[0].Directions.ShouldBe([D]);
            r.WithoutPhysical.PrimaryKeyDirections.ShouldBe([D, A]);
            r.PhysicalApplied.ShouldBeFalse();
            r.Findings.ShouldBeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-05")]
    public void AssetWithoutDirections_ChangedColumns_DirectionsReset()
    {
        // I403-T-S05
        var stored = Stored(["a", "b"], [D, A], Unique("uq_1", ["c"], [D]));
        var incoming = Incoming(["a", "z"], null, Unique("uq_1", ["c", "d"]));

        var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, stored, PhysicalKeySnapshot.Unsupported);

        r.Metadata.PrimaryKeyDirections.ShouldBeEmpty();
        r.Metadata.Indexes[0].Directions.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-06")]
    public void Precedence_AssetOverStoredOverPhysical_NeverSilentOverride()
    {
        // I403-T-S06: each difference yields exactly one finding
        var snapshot = Known(Key(true, true, ["a"], A), Key(false, false, ["b"], A), Key(false, false, ["c"], D));
        var stored = Stored(["a"], [D], Unique("uq_1", ["b"], [D]));
        var incoming = Incoming(["a"], null, Unique("uq_1", ["b"]), Unique("uq_2", ["c"], [A]));

        var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, stored, snapshot);

        r.PhysicalApplied.ShouldBeFalse();
        r.Metadata.PrimaryKeyDirections.ShouldBe([D]);                         // stored over physical: conflict
        r.Metadata.Indexes[0].Directions.ShouldBe([D]);                        // stored over physical: conflict
        r.Metadata.Indexes[1].Directions.ShouldBeNull();                       // asset (ascending) over physical: drift
        r.Findings.ShouldBe([
            PhysicalKeyDirectionReconciler.ConflictCode, PhysicalKeyDirectionReconciler.ConflictCode, PhysicalKeyDirectionReconciler.DriftCode]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-12")]
    public void PhysicalDirections_NeverChangeSecurityFlags_RatchetStillApplied()
    {
        // I403-T-S12 (reconciler half): only the key and index direction lists differ; every other field is the same instance or value
        var incoming = Incoming(["a"]) with
        {
            Table = new Table { Sensitivity = "RESTRICTED", RequiresFourEyes = true, IsActive = true },
            CatalogTags = ["t"]
        };

        var r = PhysicalKeyDirectionReconciler.Reconcile(incoming, null, Known(Key(true, true, ["a"], D)));

        r.Metadata.ShouldBe(incoming with { PrimaryKeyDirections = [D] });
        r.Metadata.Table.ShouldBeSameAs(incoming.Table);
        r.Metadata.PrimaryKeyColumns.ShouldBe(incoming.PrimaryKeyColumns);
        r.Metadata.ReplaceKeyDefinition.ShouldBeTrue();
    }
}
