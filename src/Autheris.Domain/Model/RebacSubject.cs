namespace Autheris.Domain.Model;

using System;
using System.Text.RegularExpressions;

/// <summary>
/// EP-08: Classification of ReBAC subject forms.
/// Individual subjects canonicalize to a bare SID/GUID/account identifier.
/// </summary>
public enum RebacSubjectKind
{
    Invalid = 0,
    Individual,
    Userset,
    Entity
}

public readonly record struct RebacSubjectParseResult(RebacSubjectKind Kind, string Canonical)
{
    public bool IsValid => Kind != RebacSubjectKind.Invalid;

    public static RebacSubjectParseResult InvalidResult => new(RebacSubjectKind.Invalid, string.Empty);
}

public static class RebacSubject
{
    private static readonly Regex SafeIndividualIdRegex = new(@"\A[a-zA-Z0-9_\-\.@|/\\$]{1,256}\z", RegexOptions.Compiled);
    private static readonly Regex SafeRelationRegex = new(@"\A[a-zA-Z0-9_\-]{1,128}\z", RegexOptions.Compiled);

    private static readonly string[] KnownEntityPrefixes =
    [
        "table:",
        "schema:",
        "column:",
        "group:",
        "role:",
        "catalog:",
        "domain:",
        "dataset:",
        "document:",
        "doc:",
        "folder:",
        "obj:",
        "repo:"
    ];

    /// <summary>
    /// Parses a ReBAC subject string according to Decision D-1:
    /// - Individual: "user:&lt;id&gt;", "sp:&lt;id&gt;", or bare colon-free SID/GUID/realm-qualified id -&gt; Canonical is bare &lt;id&gt;.
    /// - Userset: &lt;known-entity&gt;#&lt;relation&gt; -&gt; Canonical is the full userset.
    /// - Entity: &lt;known-entity-prefix&gt;&lt;id&gt; -&gt; Canonical is the full entity.
    /// - Any unrecognized or malformed string -&gt; Invalid.
    /// </summary>
    public static RebacSubjectParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RebacSubjectParseResult.InvalidResult;
        }

        var trimmed = raw.Trim();

        // 1. user:<id> and sp:<id> -> Individual (canonical is <id>)
        if (trimmed.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[5..].Trim();
            return IsValidIndividualId(id)
                ? new RebacSubjectParseResult(RebacSubjectKind.Individual, id)
                : RebacSubjectParseResult.InvalidResult;
        }

        if (trimmed.StartsWith("sp:", StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[3..].Trim();
            return IsValidIndividualId(id)
                ? new RebacSubjectParseResult(RebacSubjectKind.Individual, id)
                : RebacSubjectParseResult.InvalidResult;
        }

        // 2. Userset: <entity>#<relation> where entity starts with a known entity prefix
        if (trimmed.Contains('#'))
        {
            var hashIdx = trimmed.IndexOf('#');
            if (hashIdx <= 0 || hashIdx != trimmed.LastIndexOf('#'))
            {
                return RebacSubjectParseResult.InvalidResult;
            }

            var entityPart = trimmed[..hashIdx];
            var relationPart = trimmed[(hashIdx + 1)..];

            if (!HasKnownEntityPrefix(entityPart) || !SafeRelationRegex.IsMatch(relationPart))
            {
                return RebacSubjectParseResult.InvalidResult;
            }

            return new RebacSubjectParseResult(RebacSubjectKind.Userset, trimmed);
        }

        // 3. Known entity prefix: table:, schema:, column:, group:, role:, catalog:, etc.
        if (HasKnownEntityPrefix(trimmed))
        {
            return new RebacSubjectParseResult(RebacSubjectKind.Entity, trimmed);
        }

        // 4. Colon-free individual SID/GUID/account identifier
        if (IsValidIndividualId(trimmed))
        {
            return new RebacSubjectParseResult(RebacSubjectKind.Individual, trimmed);
        }

        return RebacSubjectParseResult.InvalidResult;
    }

    private static bool IsValidIndividualId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains(':') || id.Contains('#'))
        {
            return false;
        }

        return SafeIndividualIdRegex.IsMatch(id);
    }

    private static bool HasKnownEntityPrefix(string candidate)
    {
        foreach (var prefix in KnownEntityPrefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && candidate.Length > prefix.Length)
            {
                return true;
            }
        }

        return false;
    }
}
