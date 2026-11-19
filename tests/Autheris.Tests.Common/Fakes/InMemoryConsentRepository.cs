using Autheris.Domain.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Passive in-memory consent store. Seed with <see cref="Add"/>. Subject reads follow the SQL repository: consents
/// granted to one of the subjects that are active at the read time (plus not-yet-valid Deny consents, fail closed),
/// restricted to the tenant when one is passed. Writes and request-driven operations are not simulated and throw.
/// </summary>
public sealed class InMemoryConsentRepository : IConsentRepository
{
    private readonly object _gate = new();
    private readonly List<Consent> _consents = [];

    public InMemoryConsentRepository(params Consent[] seed)
    {
        foreach (var c in seed) { Add(c); }
    }

    public InMemoryConsentRepository Add(Consent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (_gate) { _consents.Add(consent); }
        return this;
    }

    private List<Consent> Snapshot()
    {
        lock (_gate) { return [.. _consents]; }
    }

    private static bool IsSubjectConsent(Consent c, HashSet<Sid> sids, TenantId? tenantId, DateTimeOffset at) =>
        c.GranteeSid is { } g && sids.Contains(g) && (tenantId is null || c.TenantId == tenantId.Value) &&
        (c.IsActive(at) || (c.Effect == ConsentEffect.Deny && c.ValidFrom > at));

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
        => GetActiveConsentsForSubjectsCore(subjects, table, atTime, null);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        // As the SQL repository: the tenant overloads require a tenant.
        if (tenantId is null) { throw new ArgumentException("A tenant is required.", nameof(tenantId)); }
        return GetActiveConsentsForSubjectsCore(subjects, table, atTime, tenantId);
    }

    private Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsCore(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId)
    {
        var sids = subjects.ToHashSet();
        return Task.FromResult<IReadOnlyList<Consent>>(Snapshot()
            .Where(c => (c.IsPatternGrant || c.TableIdentifier == table) && IsSubjectConsent(c, sids, tenantId, atTime))
            .ToList());
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
        => GetAllActiveConsentsForSubjectsCore(subjects, atTime, null);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        if (tenantId is null) { throw new ArgumentException("A tenant is required.", nameof(tenantId)); }
        return GetAllActiveConsentsForSubjectsCore(subjects, atTime, tenantId);
    }

    private Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsCore(IEnumerable<Sid> subjects, DateTimeOffset? atTime, TenantId? tenantId)
    {
        var sids = subjects.ToHashSet();
        var at = atTime ?? DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<Consent>>(Snapshot().Where(c => IsSubjectConsent(c, sids, tenantId, at)).ToList());
    }

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
        => Task.FromResult(Snapshot().FirstOrDefault(c => c.Id == consentId));

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
        => throw Unsupported();

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default)
        => throw Unsupported();

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw Unsupported();

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default)
        => throw Unsupported();

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
        => throw Unsupported();

    public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default)
        => throw Unsupported();

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default)
        => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new($"{nameof(InMemoryConsentRepository)} only simulates subject reads and lookup by id.");
}
