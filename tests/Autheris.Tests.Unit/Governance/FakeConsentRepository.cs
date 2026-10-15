using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.Governance;

/// <summary>In-memory consent store with a transactional <see cref="ReplaceConsentsAsync"/> (all-or-nothing) and fault injection.</summary>
internal sealed class FakeConsentRepository : IConsentRepository
{
    private readonly List<Consent> _consents = [];

    public bool FailReplace { get; set; }
    public int ReplaceCalls { get; private set; }
    public int DirectCreateCalls { get; private set; }
    public int DirectRevokeCalls { get; private set; }

    public IReadOnlyList<Consent> Active(DateTimeOffset at) => _consents.Where(c => c.IsActive(at)).ToList();

    public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, CancellationToken ct = default)
    {
        ReplaceCalls++;
        if (FailReplace)
        {
            throw new InvalidOperationException("injected fault");
        }

        foreach (var id in revokeConsentIds)
        {
            var i = _consents.FindIndex(c => c.Id == id);
            if (i >= 0) _consents[i] = Clone(_consents[i], revoked: true);
        }

        _consents.AddRange(create);
        return Task.CompletedTask;
    }

    private static Consent Clone(Consent c, bool revoked) => new()
    {
        Id = c.Id, TableId = c.TableId, TableIdentifier = c.TableIdentifier, TenantId = c.TenantId, Effect = c.Effect,
        GranteeType = c.GranteeType, GranteeSid = c.GranteeSid, RoleName = c.RoleName, ValidFrom = c.ValidFrom, ValidTo = c.ValidTo,
        IsRevoked = revoked, ColumnRules = c.ColumnRules, RowFilters = c.RowFilters
    };

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
        GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        var set = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Consent> r = _consents.Where(c => c.IsActive(atTime) && c.TableIdentifier.Equals(table)
            && c.GranteeSid is { } g && set.Contains(g.Value) && (tenantId is null || c.TenantId == tenantId.Value)).ToList();
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
        GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        var at = atTime ?? DateTimeOffset.UtcNow;
        var sids = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rs = (roles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Consent> r = _consents.Where(c => c.IsActive(at) && (tenantId is null || c.TenantId == tenantId.Value)
            && (c.GranteeType == GranteeType.Role ? c.RoleName is { } n && rs.Contains(n) : c.GranteeSid is { } g && sids.Contains(g.Value))).ToList();
        return Task.FromResult(r);
    }

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        DirectCreateCalls++;
        _consents.Add(consent);
        return Task.FromResult(consent);
    }

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
        Task.FromResult(_consents.FirstOrDefault(c => c.Id == consentId));

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        DirectRevokeCalls++;
        var i = _consents.FindIndex(c => c.Id == consentId);
        if (i >= 0) _consents[i] = Clone(_consents[i], revoked: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Consent>>(_consents.Where(c => !c.IsRevoked && c.ValidTo <= threshold).ToList());

    public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Consent>>([]);

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        Task.FromResult(false);
}
