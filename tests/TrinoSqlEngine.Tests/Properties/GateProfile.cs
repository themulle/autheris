using System.Globalization;
using FsCheck;
using FsCheck.Fluent;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// Run profile of the property gate (plan 9.3 G3/G5, 9.5, 16.5 M-5). The seeds are pinned so that a failure reproduces from the
/// log line alone. One additional random seed per run widens the explored space; it is logged and printed in every failure.
/// There is no retry anywhere: a failing property fails the run (M-5).
/// </summary>
internal static class GateProfile
{
    /// <summary>Fixed seed set (committed). A seed that exposed a defect is added to <c>Corpus/regressions</c> by its owner.</summary>
    public static readonly ulong[] PinnedSeeds = [20261010UL, 7UL, 90210UL];

    /// <summary>Profile name: <c>pr</c> (default) or <c>nightly</c> (SQLGATE_PROFILE).</summary>
    public static string Name { get; } = Environment.GetEnvironmentVariable("SQLGATE_PROFILE")?.Trim().ToLowerInvariant() is "nightly" ? "nightly" : "pr";

    public static bool Nightly => Name == "nightly";

    /// <summary>Total generated cases of one property per run: 10,000 in the PR profile, 200,000 nightly (plan 9.3 G3).</summary>
    public static int CasesPerProperty { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("SQLGATE_CASES"), out var forced) && forced > 0 ? forced : Nightly ? 200_000 : 10_000;

    /// <summary>Optional extra seed forced by the environment (reproduction of a logged random seed).</summary>
    public static ulong RandomSeed { get; } = ParseSeed(Environment.GetEnvironmentVariable("SQLGATE_SEED")) ?? (ulong)Random.Shared.NextInt64(1, long.MaxValue);

    public static IEnumerable<ulong> AllSeeds() => PinnedSeeds.Append(RandomSeed);

    /// <summary>Cases per seed, so that the total of the pinned seeds plus the random seed equals <see cref="CasesPerProperty"/>.</summary>
    public static int CasesPerSeed(int total) => Math.Max(1, total / (PinnedSeeds.Length + 1));

    private static ulong? ParseSeed(string? text) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ? seed : null;

    /// <summary>Generates <paramref name="count"/> values deterministically from <paramref name="seed"/> with sizes cycling through 1..<paramref name="maxSize"/>.</summary>
    public static IEnumerable<T> Sample<T>(Gen<T> gen, ulong seed, int count, int maxSize = 8)
    {
        const int Chunk = 250;
        for (int produced = 0, round = 0; produced < count; round++)
        {
            int take = Math.Min(Chunk, count - produced);
            int size = 1 + round % maxSize;
            var chunkSeed = new Rnd(seed + (ulong)round * 0x9E3779B97F4A7C15UL, 1UL);
            foreach (var item in gen.Sample(take, chunkSeed, size))
            {
                yield return item;
            }

            produced += take;
        }
    }
}
