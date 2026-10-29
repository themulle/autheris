namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

internal enum DbtReferenceOutcome
{
    Resolved,
    Unknown,
    Ambiguous
}

/// <summary>Outcome of a reference lookup. <see cref="LogicalName"/> is the bare model or <c>source.table</c> name (display only).</summary>
internal readonly record struct DbtReferenceResult(DbtReferenceOutcome Outcome, string? UniqueId, string LogicalName);

/// <summary>
/// Issue #392: resolves dbt <c>ref(...)</c> and <c>source(...)</c> expressions to manifest unique ids. A reference resolves only when
/// exactly one node matches (I392-SC-01): candidates in the test's <c>depends_on</c> first, then the global index, never narrowed by
/// package and never first-wins (I392-SC-02). An explicit package argument is a filter.
/// </summary>
internal sealed partial class DbtManifestReferenceResolver
{
    /// <summary>Upper bound of indexed nodes; further nodes are not indexed and resolve to Unknown (fail closed).</summary>
    public const int MaxNodes = 200_000;

    private const int MaxExpressionLength = 512;

    private sealed record Entry(string UniqueId, string Name, string? SourceName, string Package, string? Version);

    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _duplicateIds = new(StringComparer.Ordinal);

    public int Count => _byId.Count;

    public void RegisterNode(string uniqueId, string name, string? package, string? version) =>
        Register(new Entry(uniqueId, name, null, package ?? PackageOf(uniqueId), version));

    public void RegisterSource(string uniqueId, string sourceName, string name, string? package) =>
        Register(new Entry(uniqueId, name, sourceName, package ?? PackageOf(uniqueId), null));

    public bool Contains(string uniqueId) => _byId.ContainsKey(uniqueId) && !_duplicateIds.Contains(uniqueId);

    /// <summary>Display name of a registered node (<c>name</c>); null when not registered.</summary>
    public string? NameOf(string uniqueId) => _byId.TryGetValue(uniqueId, out var e) ? e.Name : null;

    public DbtReferenceResult Resolve(string? to, IReadOnlyList<string> dependsOn)
    {
        if (string.IsNullOrWhiteSpace(to) || to.Length > MaxExpressionLength)
        {
            return new DbtReferenceResult(DbtReferenceOutcome.Unknown, null, "");
        }

        var text = to.Trim();
        string? package = null;
        string? version = null;
        string? sourceName = null;
        string name;

        var source = SourceCall().Match(text);
        var reference = source.Success ? Match.Empty : RefCall().Match(text);
        if (source.Success)
        {
            sourceName = source.Groups["s"].Value;
            name = source.Groups["t"].Value;
        }
        else if (reference.Success)
        {
            var first = reference.Groups["a"].Value;
            if (reference.Groups["b"].Success)
            {
                package = first;
                name = reference.Groups["b"].Value;
            }
            else
            {
                name = first;
            }

            if (reference.Groups["v"].Success)
            {
                version = reference.Groups["v"].Value.Trim('\'', '"');
            }
        }
        else if (_byId.TryGetValue(text, out var byId) && !_duplicateIds.Contains(text))
        {
            return new DbtReferenceResult(DbtReferenceOutcome.Resolved, text, byId.Name);
        }
        else if (BareName().IsMatch(text))
        {
            name = text;
        }
        else
        {
            return new DbtReferenceResult(DbtReferenceOutcome.Unknown, null, "");
        }

        var logical = sourceName is null ? name : $"{sourceName}.{name}";

        string? found = null;
        var count = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in dependsOn)
        {
            if (id is not null && seen.Add(id) && _byId.TryGetValue(id, out var entry) && Matches(entry, name, sourceName, package, version))
            {
                found = id;
                count++;
            }
        }

        if (count == 0 && _byName.TryGetValue(name, out var list))
        {
            foreach (var entry in list)
            {
                if (Matches(entry, name, sourceName, package, version))
                {
                    found = entry.UniqueId;
                    count++;
                }
            }
        }

        if (count > 0 && found is not null && _duplicateIds.Contains(found))
        {
            count = 2;
        }

        return count switch
        {
            0 => new DbtReferenceResult(DbtReferenceOutcome.Unknown, null, logical),
            1 => new DbtReferenceResult(DbtReferenceOutcome.Resolved, found, logical),
            _ => new DbtReferenceResult(DbtReferenceOutcome.Ambiguous, null, logical)
        };
    }

    private static bool Matches(Entry entry, string name, string? sourceName, string? package, string? version)
    {
        if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (sourceName is not null)
        {
            return string.Equals(entry.SourceName, sourceName, StringComparison.OrdinalIgnoreCase);
        }

        return entry.SourceName is null &&
               (package is null || string.Equals(entry.Package, package, StringComparison.OrdinalIgnoreCase)) &&
               (version is null || string.Equals(entry.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    private void Register(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.UniqueId) || string.IsNullOrWhiteSpace(entry.Name))
        {
            return;
        }

        if (_byId.ContainsKey(entry.UniqueId))
        {
            // The same unique id twice is itself ambiguous: it never resolves.
            _duplicateIds.Add(entry.UniqueId);
            return;
        }

        if (_byId.Count >= MaxNodes)
        {
            return;
        }

        _byId[entry.UniqueId] = entry;
        if (!_byName.TryGetValue(entry.Name, out var list))
        {
            list = [];
            _byName[entry.Name] = list;
        }

        list.Add(entry);
    }

    private static string PackageOf(string uniqueId)
    {
        var parts = uniqueId.Split('.', 3);
        return parts.Length >= 2 ? parts[1] : "";
    }

    [GeneratedRegex(@"\bsource\s*\(\s*(?<q1>['""])(?<s>[^'""]+)\k<q1>\s*,\s*(?<q2>['""])(?<t>[^'""]+)\k<q2>\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SourceCall();

    [GeneratedRegex(@"\bref\s*\(\s*(?<q1>['""])(?<a>[^'""]+)\k<q1>\s*(?:,\s*(?<q2>['""])(?<b>[^'""]+)\k<q2>\s*)?(?:,\s*v(?:ersion)?\s*=\s*(?<v>\d+|'[^']+'|""[^""]+""))?\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RefCall();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BareName();
}
