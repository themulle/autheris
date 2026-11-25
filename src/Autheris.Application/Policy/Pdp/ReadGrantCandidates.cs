namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.RowScopes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The tables an active Allow read consent of a subject set targets, directly or by a pattern grant. This is a pre-filter for
/// set-based callers: it never allows anything. A table outside the set cannot be allowed (zero trust: the consent resolver denies a
/// read without such a consent, and an access profile never creates an allow), so a caller only needs to ask
/// <see cref="IAccessDecisionPoint"/> about the candidates; the decision point still decides every one of them.
/// </summary>
public sealed class ReadGrantCandidates
{
    private readonly HashSet<TableIdentifier> _targeted;
    private readonly List<ObjectPatternMatcher> _patterns;

    private ReadGrantCandidates(HashSet<TableIdentifier> targeted, List<ObjectPatternMatcher> patterns, DateTimeOffset nextChange, long estimatedBytes)
    {
        _targeted = targeted;
        _patterns = patterns;
        NextChange = nextChange;
        EstimatedBytes = estimatedBytes;
    }

    /// <summary>
    /// Issue #1044: the earliest time after the read at which one of the subject's consents starts or ends (an expiry, the effective end
    /// of a clamped grant, or a scheduled deny); <see cref="DateTimeOffset.MaxValue"/> when none does. A view derived from these candidates
    /// must not be used from then on.
    /// </summary>
    public DateTimeOffset NextChange { get; }

    /// <summary>Issue #1044: the number of directly targeted tables and patterns (memory estimate of a cached view).</summary>
    public int EstimatedCount => _targeted.Count + _patterns.Count;

    /// <summary>Estimated 64-bit managed heap bytes held by this candidate set, including unshared identifier strings (#1063 §4e).</summary>
    internal long EstimatedBytes { get; }

    /// <summary>Every table is a candidate (consent bypass).</summary>
    public static ReadGrantCandidates All { get; } = new([], [], DateTimeOffset.MaxValue, 256L) { IsAll = true };

    /// <summary>No table is a candidate.</summary>
    public static ReadGrantCandidates Empty { get; } = new([], [], DateTimeOffset.MaxValue, 256L);

    private bool IsAll { get; init; }

    internal bool ContainsTargeted(TableIdentifier table) => _targeted.Contains(table);

    /// <summary>Reads the active consents of <paramref name="subjects"/> in <paramref name="tenant"/> once and keeps the Allow grant targets.</summary>
    public static async Task<ReadGrantCandidates> LoadAsync(
        IConsentRepository consentRepository,
        IEnumerable<Sid> subjects,
        DateTimeOffset now,
        TenantId? tenant,
        CancellationToken ct,
        bool includeWriteOnly = false,
        bool trackScheduledAllows = true)
    {
        ArgumentNullException.ThrowIfNull(consentRepository);
        var subjectList = subjects as IReadOnlyCollection<Sid> ?? subjects.ToList();
        var consents = includeWriteOnly && tenant is { Value.Length: > 0 } headerTenant
            ? await consentRepository.GetAllActiveConsentHeadersForSubjectsAsync(subjectList, now, headerTenant, ct).ConfigureAwait(false)
            : await consentRepository.GetAllActiveConsentsForSubjectsAsync(subjectList, roles: null, now, tenant, ct).ConfigureAwait(false);

        var targeted = new HashSet<TableIdentifier>();
        var patterns = new List<ObjectPatternMatcher>();
        var nextChange = DateTimeOffset.MaxValue;
        foreach (var consent in consents)
        {
            var boundary = consent.ValidFrom > now ? consent.ValidFrom : consent.EffectiveValidTo;
            if (boundary > now && boundary < nextChange) nextChange = boundary;

            if (consent.Effect != ConsentEffect.Allow ||
                consent.GrantKind != PermissionGrantKind.Data ||
                (tenant is { } expectedTenant && consent.TenantId != expectedTenant) ||
                !consent.IsActive(now) ||
                (!includeWriteOnly && consent.WriteRules.Count > 0 && consent.ColumnRules.Count == 0))
            {
                continue;
            }

            if (!consent.IsPatternGrant) targeted.Add(consent.TableIdentifier);
            else if (ObjectPatternMatcher.TryParse(consent.ObjectPattern, out var matcher, out _)) patterns.Add(matcher!);
        }

        if (trackScheduledAllows && tenant is { } scheduledTenant && !string.IsNullOrWhiteSpace(scheduledTenant.Value))
        {
            if (await consentRepository.GetNextScheduledAllowAsync(subjectList, now, scheduledTenant, ct).ConfigureAwait(false) is { } scheduledAllow &&
                scheduledAllow > now && scheduledAllow < nextChange)
                nextChange = scheduledAllow;
        }

        return new ReadGrantCandidates(targeted, patterns, nextChange, ComputeEstimatedBytes(targeted, patterns));
    }

    internal static long EstimateStringBytes(string? value) =>
        string.IsNullOrEmpty(value) ? 0L : ((22L + (value.Length * 2L) + 7L) & ~7L);

    internal static long EstimateIdentifierStringsBytes(TableIdentifier id) =>
        EstimateStringBytes(id.Domain) + EstimateStringBytes(id.Schema) + EstimateStringBytes(id.TableName);

    private static long ComputeEstimatedBytes(HashSet<TableIdentifier> targeted, List<ObjectPatternMatcher> patterns)
    {
        // HashSet<TableIdentifier>.Entry is 32 bytes (3 string refs + 2 ints) + 4B bucket slot across ~2x prime capacity -> ~80B per entry.
        var bytes = 256L + (targeted.Count * 80L) + (patterns.Count * 256L);
        foreach (var id in targeted)
        {
            bytes += EstimateIdentifierStringsBytes(id);
        }

        return bytes;
    }

    /// <summary>True when <paramref name="table"/> may be allowed and must be decided; false when no Allow read consent targets it.</summary>
    public bool IsCandidate(TableMetadata table) =>
        IsAll || _targeted.Contains(table.Identifier) || _patterns.Exists(p => p.Matches(table));
}
