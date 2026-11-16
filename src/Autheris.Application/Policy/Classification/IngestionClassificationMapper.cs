namespace Autheris.Application.Policy.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public static class IngestionClassificationMapper
{
    public const string IngestionSource = "INGESTION";

    private static readonly Dictionary<string, string> SafeSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GDPR"] = ClassificationScopes.PiiCode, ["PERSONAL"] = ClassificationScopes.PiiCode,
        ["PERSONAL_DATA"] = ClassificationScopes.PiiCode, ["GDPR_ART9"] = ClassificationScopes.PiiSpecialCode,
        ["ART9"] = ClassificationScopes.PiiSpecialCode, ["SPECIAL_CATEGORY"] = ClassificationScopes.PiiSpecialCode,
        ["SECRET"] = ClassificationScopes.CredentialsCode, ["SECRETS"] = ClassificationScopes.CredentialsCode,
        ["API_KEY"] = ClassificationScopes.CredentialsCode, ["RESTRICTED"] = ClassificationScopes.ConfidentialCode
    };

    public static async Task<IReadOnlySet<string>> LoadActiveCodesAsync(IClassificationRepository repo, CancellationToken ct = default)
    {
        var defs = await repo.GetClassificationsAsync(null, includeInactive: false, ct).ConfigureAwait(false);
        return defs.Count == 0
            ? new HashSet<string>(ClassificationAccessResolver.BuiltInCatalog(ClassificationScopes.Global).Definitions.Keys, StringComparer.OrdinalIgnoreCase)
            : defs.Where(static d => d.IsActive && ClassificationScopes.IsGlobal(d.Scope)).Select(static d => d.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static (IReadOnlyList<string> Codes, IReadOnlyList<string> Unknown) MapDbtMeta(IReadOnlyDictionary<string, string>? meta, IReadOnlySet<string> activeCodes)
    {
        if (meta is null || meta.Count == 0) return ([], []);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        if (IsTrue(meta, "pii") || IsTrue(meta, "is_pii")) codes.Add(ClassificationScopes.PiiCode);
        foreach (var key in new[] { "classification", "classifications" })
        {
            if (!meta.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var tok in raw.Trim().TrimStart('[').TrimEnd(']').Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ResolveToken(tok, activeCodes, codes, unknown);
        }
        return FinalizeResolved(codes, unknown);
    }

    public static (IReadOnlyList<string> Codes, IReadOnlyList<string> Unknown) MapOpenMetadataTags(
        IEnumerable<string> tagFqns, IReadOnlyCollection<string> art9Tags, IReadOnlyCollection<string> piiTags, IReadOnlySet<string> activeCodes)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var rawTag in tagFqns)
        {
            if (string.IsNullOrWhiteSpace(rawTag)) continue;
            var tag = rawTag.Trim();
            if (art9Tags.Any(a => string.Equals(tag, a, StringComparison.OrdinalIgnoreCase))) codes.Add(ClassificationScopes.PiiSpecialCode);
            else if (piiTags.Any(p => string.Equals(tag, p, StringComparison.OrdinalIgnoreCase)) || string.Equals(tag, "PII", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("PII.", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("PersonalData.", StringComparison.OrdinalIgnoreCase)) codes.Add(ClassificationScopes.PiiCode);
            else if (ExtractCandidate(tag) is { } candidate) ResolveToken(candidate, activeCodes, codes, unknown);
        }
        return FinalizeResolved(codes, unknown);
    }

    public static async Task SyncColumnAsync(
        IClassificationRepository repo, string sourceLabel, string targetName, Guid colId,
        IReadOnlyList<string> codes, IReadOnlyList<string> unknown, string assignedBy, DateTimeOffset now,
        List<string> warnings, ILogger logger, CancellationToken ct)
    {
        if (unknown.Count > 0)
        {
            var warning = $"{sourceLabel} '{targetName}': unknown classification code(s) [{string.Join(", ", unknown)}] ignored (fail-closed, not mapped to PUBLIC).";
            logger.LogWarning("{Warning}", warning);
            warnings.Add(warning);
        }
        if (codes.Count == 0) return;
        var existing = await repo.GetColumnClassificationsAsync(colId.ToString(), ClassificationScopes.Global, ct).ConfigureAwait(false);
        foreach (var code in codes.Where(c => !existing.Any(r => string.Equals(r.Code, c, StringComparison.OrdinalIgnoreCase))))
        {
            await repo.AssignColumnClassificationAsync(new ColumnClassification
            {
                Scope = ClassificationScopes.Global, TableColumnId = colId.ToString(), Code = code, Source = IngestionSource, AssignedBy = assignedBy, AssignedAt = now
            }, ct).ConfigureAwait(false);
        }
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>) FinalizeResolved(HashSet<string> codes, List<string> unknown)
    {
        if (unknown.Count > 0) { codes.Remove(ClassificationScopes.PublicCode); if (codes.Count == 0) codes.Add(ClassificationScopes.ConfidentialCode); }
        return (codes.Order(StringComparer.Ordinal).ToList(), unknown);
    }

    private static void ResolveToken(string token, IReadOnlySet<string> activeCodes, HashSet<string> codes, List<string> unknown)
    {
        var norm = token.Trim().Trim('"', '\'').ToUpperInvariant();
        if (string.IsNullOrEmpty(norm)) return;
        if (SafeSynonyms.TryGetValue(norm, out var syn) && activeCodes.Contains(syn)) codes.Add(syn);
        else if (activeCodes.Contains(norm)) codes.Add(norm);
        else unknown.Add(norm);
    }

    private static string? ExtractCandidate(string tag)
    {
        foreach (var prefix in new[] { "Classification.", "DataClassification.", "Autheris.Classification." })
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return tag[prefix.Length..];
        return !tag.Contains('.', StringComparison.Ordinal) && ClassificationHierarchyValidator.IsValidClassificationCode(tag) ? tag : null;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out var val) && (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(val, "1", StringComparison.Ordinal));
}
