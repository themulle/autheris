namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;

/// <summary>Who the caller is for attribute purposes (BC-129-01). <see cref="None"/> resolves every attribute as <c>Missing</c>.</summary>
public enum CallerSubjectKind
{
    None = 0,
    User = 1,
    Application = 2
}

/// <summary>Immutable subject key kinds. Never a UPN, mail or display name (V-129-1).</summary>
public enum SubjectKeyKind
{
    EntraObjectId = 0,
    WindowsSid = 1,
    Subject = 2
}

/// <summary>Outcome of one attribute resolution. Every non-<see cref="Resolved"/> value converges on the same constant predicate (A-11).</summary>
public enum CallerAttributeOutcome
{
    Resolved = 0,
    Missing = 1,
    Failed = 2,
    OverCardinality = 3,
    UnknownAttribute = 4,
    NoSubject = 5
}

/// <summary>
/// The caller as the resolver sees it: a gateway-owned, immutable snapshot (F-1). It carries the claims only so that the claims provider
/// can check issuer and scheme of each claim; it is used for resolution only and never logged, hashed into cache keys or serialized.
/// <see cref="ToString"/> prints no identity data (ST-129-22).
/// </summary>
public sealed record CallerSubject(
    TenantId Tenant,
    CallerSubjectKind Kind,
    string? ApplicationId,
    IReadOnlyList<Claim> Claims,
    string? Issuer,
    string? AuthenticationType,
    FrozenDictionary<SubjectKeyKind, string>? Keys = null)
{
    public static CallerSubject NoSubject(TenantId tenant) => new(tenant, CallerSubjectKind.None, null, [], null, null);

    /// <summary>The immutable key of <paramref name="kind"/>, or null when the caller has none of that kind.</summary>
    public string? KeyFor(SubjectKeyKind kind) => Keys is not null && Keys.TryGetValue(kind, out var key) ? key : null;

    /// <summary>The caller-scope state of this request (memo, BC-129-07); null for long-lived principals (CDC, webhooks, subscriptions).</summary>
    internal CallerAttributeRequestState? RequestState { get; init; }

    /// <summary>
    /// Reserves bind items for the attribute expansions of this request (BC-129-09). Without a request scope (long-lived subjects) the
    /// sum cannot be tracked and the call returns true; the per-decision cap still applies at the bind site.
    /// </summary>
    public bool TryReserveBinds(int items, int cap) => RequestState?.TryReserve(items, cap) ?? true;

    public override string ToString() => $"CallerSubject {{ Kind = {Kind} }}";

    public bool Equals(CallerSubject? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>The values of one resolved attribute. Immutable; <see cref="ToString"/> prints type and count only (INV-16).</summary>
public sealed class CallerAttributeValue
{
    private readonly object[] _elements;

    public CallerAttributeValue(IReadOnlyList<object> elements, SqlParameterType elementType)
    {
        ArgumentNullException.ThrowIfNull(elements);
        _elements = elements.ToArray();
        ElementType = elementType;
    }

    public SqlParameterType ElementType { get; }

    public int Count => _elements.Length;

    public object this[int index] => _elements[index];

    /// <summary>Approximate heap size, for the cache size limit.</summary>
    internal long EstimatedBytes => 64 + _elements.Sum(static e => e is string s ? 32 + (2L * s.Length) : 24L);

    public override string ToString() => $"CallerAttributeValue {{ Type = {ElementType}, Count = {Count} }}";
}

/// <summary>Result of one attribute; <paramref name="ReasonCode"/> is a stable audit code (never a value, never exception text).</summary>
public sealed record CallerAttributeResult(CallerAttributeOutcome Outcome, CallerAttributeValue? Value, string? ReasonCode)
{
    /// <summary>The registry id of the definition the result belongs to (set by the resolver; audit only).</summary>
    public Guid? DefinitionId { get; init; }

    public static CallerAttributeResult Resolved(CallerAttributeValue value) => new(CallerAttributeOutcome.Resolved, value, null);

    public static CallerAttributeResult Missing(string? reason = null) => new(CallerAttributeOutcome.Missing, null, reason);

    public static CallerAttributeResult Failed(string reason) => new(CallerAttributeOutcome.Failed, null, reason);

    public static CallerAttributeResult OverCardinality() => new(CallerAttributeOutcome.OverCardinality, null, "OverCardinality");

    public static CallerAttributeResult Unknown() => new(CallerAttributeOutcome.UnknownAttribute, null, "UnknownAttribute");

    public static CallerAttributeResult NoSubject() => new(CallerAttributeOutcome.NoSubject, null, "NoSubject");

    public override string ToString() => $"CallerAttributeResult {{ Outcome = {Outcome}, Reason = {ReasonCode} }}";
}

/// <summary>Name to result. <see cref="Outcomes"/> exposes names, outcomes and reason codes only.</summary>
public sealed class CallerAttributeSet
{
    public static readonly CallerAttributeSet Empty = new(new Dictionary<string, CallerAttributeResult>());

    private readonly Dictionary<string, CallerAttributeResult> _results;

    public CallerAttributeSet(IReadOnlyDictionary<string, CallerAttributeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = new Dictionary<string, CallerAttributeResult>(results, StringComparer.Ordinal);
    }

    public bool IsEmpty => _results.Count == 0;

    public bool TryGet(string name, out CallerAttributeResult result)
    {
        if (_results.TryGetValue(name, out var found))
        {
            result = found;
            return true;
        }

        result = CallerAttributeResult.Unknown();
        return false;
    }

    /// <summary>The audit entries of this set: registry id (or <c>unregistered</c>), outcome and reason; never a name, a value or a subject (BC-129-15).</summary>
    public IReadOnlyList<CallerAttributeAuditEntry> AuditEntries() =>
        _results.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new CallerAttributeAuditEntry(p.Value.DefinitionId?.ToString("D") ?? "unregistered", p.Value.Outcome.ToString(), p.Value.ReasonCode))
            .ToList();

    public IReadOnlyList<(string Name, CallerAttributeOutcome Outcome, string? ReasonCode)> Outcomes =>
        _results.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Outcome, p.Value.ReasonCode)).ToList();

    public override string ToString() => $"CallerAttributeSet {{ Count = {_results.Count} }}";
}

/// <summary>A source of attribute values. Registered through DI (<c>IEnumerable&lt;ICallerAttributeProvider&gt;</c>); WP-SC-05/07 add kinds later.</summary>
public interface ICallerAttributeProvider
{
    string Kind { get; }

    ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct);
}

/// <summary>Resolves the named attributes of one caller. The only caller of <see cref="ICallerAttributeProvider"/>.</summary>
public interface ICallerAttributeResolver
{
    /// <summary>False when <c>Gateway:CallerAttributes:Enabled</c> is off: callers skip the resolution and keep the constants.</summary>
    bool IsEnabled { get; }

    /// <summary>Padding of list expansions (<c>PadListsToBuckets</c>, the hard cap rounded up to a power of two).</summary>
    CallerAttributePadding Padding { get; }

    /// <summary>Bind items all attribute expansions of one request may use together (BC-129-09).</summary>
    int BindReserve { get; }

    /// <summary>Builds the subject (BC-129-01). <paramref name="longLived"/> marks CDC, webhook and subscription principals: no request memo (BC-129-07).</summary>
    CallerSubject CreateSubject(ClaimsPrincipal? principal, TenantId tenant, bool longLived = false);

    ValueTask<CallerAttributeSet> ResolveAsync(CallerSubject subject, IReadOnlyCollection<string> names, CancellationToken ct);
}

/// <summary>Immutable view of the active registry; definitions of other tenants are never returned.</summary>
public sealed class CallerAttributeSnapshot
{
    public static readonly CallerAttributeSnapshot Empty = new(0, [], []);

    private readonly FrozenDictionary<(string Tenant, string Name), CallerAttributeDefinition> _definitions;
    private readonly FrozenDictionary<(string Tenant, string Application, string Name), IReadOnlyList<string>> _serviceSets;

    public CallerAttributeSnapshot(long generation, IEnumerable<CallerAttributeDefinition> activeDefinitions, IEnumerable<ServiceAttributeSet> activeServiceSets)
    {
        Generation = generation;
        _definitions = activeDefinitions
            .Where(d => d.Status == FilterApprovalStatus.Active)
            .ToFrozenDictionary(d => (d.Tenant.Value, d.Name));
        _serviceSets = activeServiceSets
            .Where(s => s.Status == FilterApprovalStatus.Active)
            .ToFrozenDictionary(s => (s.Tenant.Value, CallerAttributeNameRules.CanonicalApplicationId(s.ApplicationId), s.AttributeName), s => s.Values);
    }

    public long Generation { get; }

    public bool TryGetDefinition(TenantId tenant, string name, out CallerAttributeDefinition definition) =>
        _definitions.TryGetValue((tenant.Value, name), out definition!);

    public bool TryGetServiceSet(TenantId tenant, string applicationId, string name, out IReadOnlyList<string> values) =>
        _serviceSets.TryGetValue((tenant.Value, CallerAttributeNameRules.CanonicalApplicationId(applicationId), name), out values!);

    public IEnumerable<CallerAttributeDefinition> Definitions => _definitions.Values;
}

public interface ICallerAttributeRegistry
{
    ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct);

    void Invalidate();
}

/// <summary>Hook for admin refresh and (later) the SCIM lock/deprovision event of WP-SC-07 (BC-129-14).</summary>
public interface ICallerAttributeCacheInvalidator
{
    void InvalidateSubject(TenantId tenant, string subjectKey);

    void InvalidateAttribute(TenantId tenant, string attributeName);
}

/// <summary>Name and identifier rules shared by the registry, the validators and the providers.</summary>
public static class CallerAttributeNameRules
{
    /// <summary>Application ids compare ordinally after GUID canonicalisation (lower-case "D"); anything else exactly (BC-129-02).</summary>
    public static string CanonicalApplicationId(string applicationId)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        return applicationId.Length == 36 && Guid.TryParseExact(applicationId, "D", out var guid) ? guid.ToString("D", System.Globalization.CultureInfo.InvariantCulture) : applicationId;
    }
}
