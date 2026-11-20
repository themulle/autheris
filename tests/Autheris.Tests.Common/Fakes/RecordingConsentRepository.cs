using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Writable, thread-safe in-memory <see cref="IConsentRepository"/> recording created and revoked consents for unit tests.
/// </summary>
public sealed class RecordingConsentRepository : IConsentRepository
{
    private readonly object _gate = new();
    private readonly List<Consent> _consents = [];
    private readonly List<Consent> _created = [];
    private readonly HashSet<Guid> _revokedIds = [];
    private readonly List<Guid> _revokedOrder = [];
    private readonly List<Guid> _requestedConsentRequestIds = [];
    private int _userRevokeCalls;

    public RecordingConsentRepository(params Consent[] seed)
    {
        foreach (var c in seed) { Add(c); }
    }

    public Func<Guid, bool>? OnRevokeSystemConsent { get; set; }

    public IReadOnlyList<Consent> CreatedConsents
    {
        get { lock (_gate) { return _created.ToArray(); } }
    }

    public IReadOnlyList<Guid> RevokedIds
    {
        get { lock (_gate) { return _revokedOrder.ToArray(); } }
    }

    public IReadOnlyList<Guid> RequestedConsentRequestIds
    {
        get { lock (_gate) { return _requestedConsentRequestIds.ToArray(); } }
    }

    public int UserRevokeCalls => Volatile.Read(ref _userRevokeCalls);

    public RecordingConsentRepository Add(Consent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (_gate) { _consents.Add(consent); }
        return this;
    }

    private List<Consent> ActiveSnapshot()
    {
        lock (_gate) { return _consents.Where(c => !_revokedIds.Contains(c.Id)).ToList(); }
    }

    private static bool IsSubjectConsent(Consent c, HashSet<Sid> sids, TenantId? tenantId, DateTimeOffset at) =>
        c.GranteeSid is { } g && sids.Contains(g) && (tenantId is null || c.TenantId == tenantId.Value) &&
        (c.IsActive(at) || (c.Effect == ConsentEffect.Deny && c.ValidFrom > at));

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
        => GetActiveConsentsForSubjectsCore(subjects, table, atTime, null);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        if (tenantId is null) { throw new ArgumentException("A tenant is required.", nameof(tenantId)); }
        return GetActiveConsentsForSubjectsCore(subjects, table, atTime, tenantId);
    }

    private Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsCore(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId)
    {
        var sids = subjects.ToHashSet();
        return Task.FromResult<IReadOnlyList<Consent>>(ActiveSnapshot()
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
        return Task.FromResult<IReadOnlyList<Consent>>(ActiveSnapshot().Where(c => IsSubjectConsent(c, sids, tenantId, at)).ToList());
    }

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
        => Task.FromResult(ActiveSnapshot().FirstOrDefault(c => c.Id == consentId));

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Consent>>(ActiveSnapshot()
            .Where(c => !c.IsRevoked && c.ValidTo <= threshold)
            .Take(maxResults)
            .ToList());

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        lock (_gate) { _requestedConsentRequestIds.Add(consentRequestId); }
        return Task.FromResult<IReadOnlyList<Consent>>(ActiveSnapshot()
            .Where(c => c.ConsentRequestId == consentRequestId && c.IsActive(atTime))
            .ToList());
    }

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (_gate)
        {
            _consents.Add(consent);
            _created.Add(consent);
        }

        return Task.FromResult(consent);
    }

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _userRevokeCalls);
        lock (_gate)
        {
            if (_revokedIds.Add(consentId))
            {
                _revokedOrder.Add(consentId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        if (OnRevokeSystemConsent is not null && !OnRevokeSystemConsent(consentId))
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            var match = _consents.Any(c => c.Id == consentId && c.ConsentRequestId == consentRequestId && !_revokedIds.Contains(consentId));
            if (match)
            {
                _revokedIds.Add(consentId);
                _revokedOrder.Add(consentId);
            }

            return Task.FromResult(match);
        }
    }

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
    {
        var items = ActiveSnapshot().ToList();
        return Task.FromResult(new ConsentPage(items, null));
    }

    public Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in revokeConsentIds)
            {
                if (_revokedIds.Add(id))
                {
                    _revokedOrder.Add(id);
                }
            }

            foreach (var c in create)
            {
                _consents.Add(c);
                _created.Add(c);
            }
        }

        return Task.CompletedTask;
    }
}
