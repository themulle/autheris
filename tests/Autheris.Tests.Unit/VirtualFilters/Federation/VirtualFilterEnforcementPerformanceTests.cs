namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 CR-08 (NFR-7): the per-row probe of the Tier 3 enforcement does not allocate per row (no closure, no value
/// buffer, no canonical array, no boxing on the single-column path). 50,000 upstream rows against 10,000 authorized
/// keys; the benchmark in <c>benchmarks/Autheris.Benchmarks</c> reports the timing.
/// </summary>
public sealed class VirtualFilterEnforcementPerformanceTests(ITestOutputHelper output)
{
    private const int Rows = 50_000;
    private const int Keys10k = 10_000;

    /// <summary>The probe itself may allocate at most this many bytes per row (the result list is preallocated).</summary>
    private const double MaxBytesPerRow = 16;

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> MakeRows(Func<int, object?> id) =>
        Enumerable.Range(0, Rows).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = id(i), ["name"] = "n" }).ToArray();

    private static (double BytesPerRow, double Millis, int Kept) Measure(VirtualFilterEnforcement enforcement, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        enforcement.Apply(rows);   // warm up (JIT, tiered compilation)
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var kept = enforcement.Apply(rows);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The result list is allocated with the row count as capacity: one reference per row.
        double probeBytes = allocated - (double)rows.Count * IntPtr.Size;
        return (probeBytes / rows.Count, sw.Elapsed.TotalMilliseconds, kept.Count);
    }

    [Theory]
    [InlineData("integer", VirtualFilterKeyType.IntegerNumber)]
    [InlineData("varchar", VirtualFilterKeyType.Text)]
    public void SingleColumn_50kRows_10kKeys_DoesNotAllocatePerRow(string catalogType, VirtualFilterKeyType keyType)
    {
        object Key(int i) => keyType == VirtualFilterKeyType.Text ? "K" + i : (long)i;
        var keys = Enumerable.Range(0, Keys10k).Select(i => new object?[] { Key(i * 2) }).ToArray();   // even keys are authorized
        var enforcement = Enforcement(Customers(catalogType), (PairById(), VirtualFilterEnforcementFixture.Keys([keyType], keys)));
        var rows = MakeRows(i => Key(i % 20_000));

        var (bytesPerRow, millis, kept) = Measure(enforcement, rows);

        output.WriteLine($"single column {catalogType}: {millis:F2} ms for {Rows} rows, {bytesPerRow:F1} B/row allocated by the probe, kept {kept}");
        kept.ShouldBe(Rows / 2);
        bytesPerRow.ShouldBeLessThanOrEqualTo(MaxBytesPerRow);
    }

    [Fact]
    public void CompositeKey_50kRows_AllocatesOnlyTheCanonicalKeyPerProbe()
    {
        var table = FederatedVfPipelineHarness.HttpTable("crm_api", "public", "contracts", "client_id", "region", "name");
        var filter = KeyFilter("vf_composite", "client_id", "region");
        var pair = new AppliedVirtualFilterBinding(filter, new FilterBinding { FilterName = "vf_composite", TargetPattern = "crm_api.public.contracts" }, 1);
        var types = new[] { VirtualFilterKeyType.Text, VirtualFilterKeyType.Text };
        var enforcement = Enforcement(table, (pair, VirtualFilterEnforcementFixture.Keys(types, Enumerable.Range(0, Keys10k).Select(i => new object?[] { "C" + (i * 2), "EU" }).ToArray())));
        var rows = Enumerable.Range(0, Rows)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["client_id"] = "C" + (i % 20_000), ["region"] = "EU" })
            .ToArray();

        var (bytesPerRow, millis, kept) = Measure(enforcement, rows);

        output.WriteLine($"composite: {millis:F2} ms for {Rows} rows, {bytesPerRow:F1} B/row allocated by the probe, kept {kept}");
        kept.ShouldBe(Rows / 2);
        bytesPerRow.ShouldBeLessThanOrEqualTo(48);   // one canonical array (2 references + header), nothing else
    }
}
