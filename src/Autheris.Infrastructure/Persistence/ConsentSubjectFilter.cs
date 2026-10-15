using Autheris.Domain.Common;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// WP-SC-01: helpers shared by the three governance repositories for the indexed consent query. The SQL filter is only a
/// prefilter: the C# filter on the loaded rows stays authoritative, so the result is identical to the former in-memory filter.
/// </summary>
internal static class ConsentSubjectFilter
{
    /// <summary>Largest number of subject parameters per statement (SQL Server allows 2,100 parameters in total).</summary>
    internal const int MaxSubjectsPerStatement = 1000;

    /// <summary>
    /// The normalised form stored in <c>CONSENTS.grantee_sid_norm</c> and used for the lookup (upper-case, invariant). Equal under
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> implies equal here, so the prefilter never drops a consent that the C# filter keeps.
    /// </summary>
    internal static string Normalize(string sid) => sid.ToUpperInvariant();

    internal static string? NormalizeOrNull(Sid? sid) => sid is { } s ? Normalize(s.Value) : null;

    /// <summary>
    /// Prefilter (SQLite and SQL Server) (<c>IN (@s0, ...)</c> over the chunk; no IN list when the chunk is empty). Role consents and rows without a
    /// normalised SID always pass.
    /// </summary>
    internal static string InListPredicate(int subjectCount) => subjectCount == 0
        ? " AND (c.grantee_type = 'Role' OR c.grantee_sid_norm IS NULL)"
        : " AND (c.grantee_type = 'Role' OR c.grantee_sid_norm IS NULL OR c.grantee_sid_norm IN ("
          + string.Join(",", Enumerable.Range(0, subjectCount).Select(i => "@s" + i)) + "))";

    /// <summary>Parameter value for <c>grantee_sid_norm</c>: the normalised SID, or <see cref="DBNull"/> when there is none (role consents).</summary>
    internal static object NormalizeParameter(object? sid) => sid is string s ? Normalize(s) : DBNull.Value;

    /// <summary>Distinct normalised subject keys, in a stable order.</summary>
    internal static string[] DistinctNormalized(IEnumerable<Sid> subjects)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var s in subjects)
        {
            if (set.Add(Normalize(s.Value)))
            {
                list.Add(Normalize(s.Value));
            }
        }

        return list.ToArray();
    }

    internal static IEnumerable<ArraySegment<string>> Chunks(string[] keys, int size = MaxSubjectsPerStatement)
    {
        if (keys.Length == 0)
        {
            yield return new ArraySegment<string>(keys);
            yield break;
        }

        for (var i = 0; i < keys.Length; i += size)
        {
            yield return new ArraySegment<string>(keys, i, Math.Min(size, keys.Length - i));
        }
    }
}
