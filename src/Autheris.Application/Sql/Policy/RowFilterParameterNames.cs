namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Security;

/// <summary>
/// SEC-ADG-02 item 2: the string consumers of a row filter fail closed on a duplicate parameter name. The producers key their values by the
/// marker (<c>@__gql_rls_0</c>) and the providers match the name case-insensitively and without the marker prefix, so two keys that differ only
/// in the prefix or the case, or a key that names a parameter the consumer binds itself (the tenant, a pagination value), would let the provider
/// pick one of two values silently.
/// </summary>
public static class RowFilterParameterNames
{
    /// <summary>The name a provider binds: the marker prefix (<c>@</c>, <c>:</c>) removed.</summary>
    public static string Bare(string name) => name.TrimStart('@', ':');

    /// <summary>
    /// Returns the values keyed by their bare name. <paramref name="taken"/> holds the bare names the caller already bound and receives the
    /// names of <paramref name="parameters"/>.
    /// </summary>
    /// <exception cref="SecurityException">Two keys name the same parameter, or a key names a parameter in <paramref name="taken"/>.</exception>
    public static IReadOnlyList<KeyValuePair<string, object?>> Checked(IReadOnlyDictionary<string, object?>? parameters, ISet<string>? taken = null)
    {
        if (parameters is null || parameters.Count == 0) return [];
        taken ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KeyValuePair<string, object?>>(parameters.Count);
        foreach (var (name, value) in parameters)
        {
            string bare = Bare(name);
            if (bare.Length == 0 || !taken.Add(bare))
            {
                throw new SecurityException("A row filter parameter name is empty, duplicated or already bound by the consumer (fail-closed).");
            }

            result.Add(new KeyValuePair<string, object?>(bare, value));
        }

        return result;
    }
}
