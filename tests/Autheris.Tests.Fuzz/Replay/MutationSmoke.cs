using System.Text;
using Autheris.Tests.Fuzz.Target;

namespace Autheris.Tests.Fuzz.Replay;

/// <summary>
/// Local stand-in for AFL (no AFL needed): a seeded loop of mutated seeds through the same harness entry (<see cref="FuzzRunner.RunOne"/>).
/// Deterministic for a given seed and corpus; findings are returned with their input.
/// </summary>
public static class MutationSmoke
{
    private static readonly string[] Fragments =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "ON", "GROUP BY", "ORDER BY", "WITH RECURSIVE", "UNION ALL", "(", ")", ",", "'", "\"", "/*", "--", "*",
        "OVER (", "CASE WHEN", "CAST(", "UNNEST(", "LATERAL", "MERGE INTO", "INSERT INTO", "UPDATE", "DELETE FROM", "VALUES", "NULL", "1", "orders",
        "email", "tenant_id", "region", "$$", "E'", "U&'", "AT TIME ZONE", "INTERVAL", "ROW(", "ARRAY[", "->", "||", "<>",
    ];

    public sealed record Finding(int Index, FuzzRunResult Result, byte[] Input);

    public sealed record SmokeReport(int Executed, int Accepted, IReadOnlyList<Finding> Findings);

    public static SmokeReport Run(IReadOnlyList<byte[]> seeds, int count, int seed)
    {
        if (seeds.Count == 0)
        {
            throw new InvalidOperationException("no seeds");
        }

        var random = new Random(seed);
        var findings = new List<Finding>();
        var accepted = 0;
        for (var i = 0; i < count; i++)
        {
            var input = Mutate(seeds, random);
            var result = FuzzRunner.RunOne(input);
            if (result.Outcome == FuzzOutcome.Accepted)
            {
                accepted++;
            }
            else
            {
                findings.Add(new Finding(i, result, input));
            }
        }

        return new SmokeReport(count, accepted, findings);
    }

    public static byte[] Mutate(IReadOnlyList<byte[]> seeds, Random random)
    {
        var data = new List<byte>(seeds[random.Next(seeds.Count)]);
        var rounds = 1 + random.Next(4);
        for (var r = 0; r < rounds; r++)
        {
            switch (random.Next(8))
            {
                case 0 when data.Count > 0:
                    data[random.Next(data.Count)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data.Insert(random.Next(data.Count + 1), (byte)random.Next(256));
                    break;
                case 2 when data.Count > 1:
                {
                    var start = random.Next(data.Count);
                    data.RemoveRange(start, Math.Min(data.Count - start, 1 + random.Next(8)));
                    break;
                }

                case 3:
                    data.InsertRange(random.Next(data.Count + 1), Encoding.UTF8.GetBytes(" " + Fragments[random.Next(Fragments.Length)] + " "));
                    break;
                case 4 when data.Count > 1:
                {
                    var start = random.Next(data.Count);
                    var length = Math.Min(data.Count - start, 1 + random.Next(32));
                    data.InsertRange(random.Next(data.Count + 1), data.GetRange(start, length));
                    break;
                }

                case 5:
                {
                    var other = seeds[random.Next(seeds.Count)];
                    var cut = random.Next(other.Length + 1);
                    data.InsertRange(random.Next(data.Count + 1), other.AsSpan(cut).ToArray());
                    break;
                }

                case 6 when data.Count > 0:
                    data.RemoveRange(random.Next(data.Count), 1);
                    break;
                case 7 when data.Count > 0:
                {
                    // Deep nesting: repeat an opening bracket many times.
                    data.InsertRange(random.Next(data.Count + 1), Enumerable.Repeat((byte)'(', 50 + random.Next(2000)));
                    break;
                }
            }

            if (data.Count > FuzzTarget.MaxInputBytes)
            {
                data.RemoveRange(FuzzTarget.MaxInputBytes, data.Count - FuzzTarget.MaxInputBytes);
            }
        }

        return [.. data];
    }
}
