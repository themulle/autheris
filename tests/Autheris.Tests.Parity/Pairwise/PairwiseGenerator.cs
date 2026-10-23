namespace Autheris.Tests.Parity.Pairwise;

public sealed record Factor(string Name, IReadOnlyList<string> Values);

/// <summary>
/// In-repo greedy IPOG-style pairwise generator (D-14, no dependency): in-parameter-order growth, horizontal (extend the existing tests by the
/// value that covers the most uncovered pairs) then vertical (new tests for the pairs left). <see cref="System.Random"/> seeded by the caller
/// is used only to break ties, so the same seed always gives the same case list.
/// </summary>
public static class PairwiseGenerator
{
    private const int Unset = -1;

    /// <summary>
    /// Returns value indexes per test, one entry per factor in declaration order. <paramref name="forbidden"/> receives an assignment
    /// (<c>-1</c> = not set) and returns true when the combination is invalid; a pair that is forbidden on its own is not required.
    /// </summary>
    public static IReadOnlyList<int[]> Generate(IReadOnlyList<Factor> factors, int seed, Func<int[], bool>? forbidden = null)
    {
        ArgumentNullException.ThrowIfNull(factors);
        if (factors.Count < 2)
        {
            throw new ArgumentException("At least two factors are required.", nameof(factors));
        }

        forbidden ??= static _ => false;
        var rng = new Random(seed);

        // grow in descending cardinality (stable by declaration order): the largest factors fix the minimum test count
        var order = Enumerable.Range(0, factors.Count).OrderByDescending(i => factors[i].Values.Count).ThenBy(i => i).ToArray();
        var tests = new List<int[]>();
        var (a, b) = (order[0], order[1]);
        for (var i = 0; i < factors[a].Values.Count; i++)
        {
            for (var j = 0; j < factors[b].Values.Count; j++)
            {
                var t = NewTest(factors.Count);
                t[a] = i;
                t[b] = j;
                if (!forbidden(t))
                {
                    tests.Add(t);
                }
            }
        }

        for (var step = 2; step < order.Length; step++)
        {
            var f = order[step];
            var done = order[..step];
            var uncovered = new HashSet<(int Factor, int Value, int NewValue)>();
            foreach (var p in done)
            {
                for (var v = 0; v < factors[p].Values.Count; v++)
                {
                    for (var w = 0; w < factors[f].Values.Count; w++)
                    {
                        var probe = NewTest(factors.Count);
                        probe[p] = v;
                        probe[f] = w;
                        if (!forbidden(probe))
                        {
                            uncovered.Add((p, v, w));
                        }
                    }
                }
            }

            // horizontal growth
            foreach (var t in tests)
            {
                var best = new List<int>();
                var bestGain = -1;
                for (var w = 0; w < factors[f].Values.Count; w++)
                {
                    t[f] = w;
                    if (forbidden(t))
                    {
                        continue;
                    }

                    var gain = done.Count(p => t[p] != Unset && uncovered.Contains((p, t[p], w)));
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        best.Clear();
                    }

                    if (gain == bestGain)
                    {
                        best.Add(w);
                    }
                }

                if (best.Count == 0)
                {
                    t[f] = Unset;
                    continue;
                }

                t[f] = best.Count == 1 ? best[0] : best[rng.Next(best.Count)];
                foreach (var p in done.Where(p => t[p] != Unset))
                {
                    uncovered.Remove((p, t[p], t[f]));
                }
            }

            // vertical growth: place each remaining pair in an unset-compatible test, otherwise add a test
            foreach (var (p, v, w) in uncovered.OrderBy(x => x.Factor).ThenBy(x => x.Value).ThenBy(x => x.NewValue).ToList())
            {
                var fit = tests.FirstOrDefault(t => (t[p] == Unset || t[p] == v) && (t[f] == Unset || t[f] == w) && Fits(t, p, v, f, w, forbidden));
                if (fit is null)
                {
                    fit = NewTest(factors.Count);
                    tests.Add(fit);
                }

                fit[p] = v;
                fit[f] = w;
            }
        }

        // fill the don't-care cells with the first allowed value (deterministic, adds coverage only)
        foreach (var t in tests)
        {
            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] != Unset)
                {
                    continue;
                }

                for (var v = 0; v < factors[i].Values.Count; v++)
                {
                    t[i] = v;
                    if (!forbidden(t))
                    {
                        break;
                    }

                    t[i] = Unset;
                }

                if (t[i] == Unset)
                {
                    t[i] = 0;
                }
            }
        }

        return tests;
    }

    /// <summary>All value pairs of two different factors that no test covers and that are not forbidden on their own.</summary>
    public static IReadOnlyList<string> UncoveredPairs(IReadOnlyList<Factor> factors, IReadOnlyList<int[]> tests, Func<int[], bool>? forbidden = null)
    {
        forbidden ??= static _ => false;
        var missing = new List<string>();
        for (var i = 0; i < factors.Count; i++)
        {
            for (var j = i + 1; j < factors.Count; j++)
            {
                for (var v = 0; v < factors[i].Values.Count; v++)
                {
                    for (var w = 0; w < factors[j].Values.Count; w++)
                    {
                        var probe = NewTest(factors.Count);
                        probe[i] = v;
                        probe[j] = w;
                        if (!forbidden(probe) && !tests.Any(t => t[i] == v && t[j] == w))
                        {
                            missing.Add($"{factors[i].Name}={factors[i].Values[v]}, {factors[j].Name}={factors[j].Values[w]}");
                        }
                    }
                }
            }
        }

        return missing;
    }

    private static bool Fits(int[] t, int p, int v, int f, int w, Func<int[], bool> forbidden)
    {
        var copy = (int[])t.Clone();
        copy[p] = v;
        copy[f] = w;
        return !forbidden(copy);
    }

    private static int[] NewTest(int width)
    {
        var t = new int[width];
        Array.Fill(t, Unset);
        return t;
    }

    /// <summary>Folds the (possibly 10-digit) seed text into the 32-bit seed of <see cref="System.Random"/>.</summary>
    public static int SeedFrom(string seedText) =>
        long.TryParse(seedText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? unchecked((int)(s ^ (s >> 32)))
            : throw new ArgumentException($"Seed '{seedText}' is not a non-negative integer.", nameof(seedText));
}
