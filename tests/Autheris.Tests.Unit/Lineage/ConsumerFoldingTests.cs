namespace Autheris.Tests.Unit.Lineage;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Lineage;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>MISC-17: k-anonymity is decided per published cell on its own distinct actors; folded buckets count the true union per day.</summary>
[Trait("Category", "Security")]
public sealed class ConsumerFoldingTests
{
    private static ConsumerAppDailySummary Cell(string app, string day, int actors, params string[] keys) =>
        new(app, day, Calls: 10, RowsReturned: 100, RowsMeasuredCalls: 10, new RejectionBreakdown(), ["1.0"], ["Q"], DistinctActors: actors)
        {
            ActorKeys = keys.Length == 0 ? null : new HashSet<string>(keys)
        };

    [Fact]
    public void SingleActorDay_FoldedDespiteAppWideCount()
    {
        // The CFO repro: the app has many actors over the window, but on this day only one actor read; the cell must not publish alone.
        var (results, suppressed) = ConsumerFolding.Fold([Cell("finance-app", "2026-10-18", 1, "cfo")]);

        results.ShouldNotContain(r => r.Application == "finance-app");
        suppressed.ShouldBeTrue();
        results.Single().Calls.ShouldBe(0);
    }

    [Fact]
    public void SameActorThreeDays_FoldedBucketCountsOneActor_Suppressed()
    {
        var (results, suppressed) = ConsumerFolding.Fold(
        [
            Cell("a", "2026-10-16", 1, "same"),
            Cell("b", "2026-10-17", 1, "same"),
            Cell("c", "2026-10-18", 1, "same")
        ]);

        suppressed.ShouldBeTrue();
        results.ShouldAllBe(r => r.Calls == 0);
        results.Count.ShouldBe(3);
    }

    [Fact]
    public void FoldedBuckets_PerDay_NotMergedUnderFirstDay()
    {
        var (results, suppressed) = ConsumerFolding.Fold(
        [
            Cell("a", "2026-10-17", 2, "k1", "k2"),
            Cell("b", "2026-10-17", 1, "k3"),
            Cell("c", "2026-10-18", 2, "k4", "k5"),
            Cell("d", "2026-10-18", 1, "k6")
        ]);

        suppressed.ShouldBeFalse();
        results.Count.ShouldBe(2);
        results.Select(r => r.Day).OrderBy(d => d).ShouldBe(["2026-10-17", "2026-10-18"]);
        results.ShouldAllBe(r => r.Application == ConsumerFolding.DefaultOtherBucketName && r.DistinctActors == 3 && r.Calls == 20);
    }

    [Fact]
    public void MissingActorKeys_FoldedBucketSuppressed()
    {
        var (results, suppressed) = ConsumerFolding.Fold([Cell("a", "2026-10-18", 2), Cell("b", "2026-10-18", 2)]);

        suppressed.ShouldBeTrue();
        results.Single().Calls.ShouldBe(0);
    }

    [Fact]
    public void CellWithEnoughOwnActors_IsPublished()
    {
        var (results, suppressed) = ConsumerFolding.Fold([Cell("big", "2026-10-18", 3, "a", "b", "c")]);

        suppressed.ShouldBeFalse();
        results.Single().Application.ShouldBe("big");
    }

    [Fact]
    public void ActorKeys_NeverSerialized()
    {
        var json = JsonSerializer.Serialize(Cell("a", "2026-10-18", 1, "secret-hash"));

        json.ShouldNotContain("secret-hash");
        json.ShouldNotContain("ActorKeys");
    }
}
