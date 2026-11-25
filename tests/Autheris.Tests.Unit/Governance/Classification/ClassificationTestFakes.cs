namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

internal sealed class FakeClassificationSecretProvider : IKeyVaultSecretProvider
{
    public byte[] GetSecretBytes(string secretRef) =>
        secretRef == "test-hmac-key" ? new byte[32] : throw new KeyNotFoundException(secretRef);
}

internal sealed class AdjustableClassificationTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

internal sealed class NoOpMandatoryRowFilterResolver : IMandatoryRowFilterResolver
{
    public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default) =>
        ValueTask.FromResult(MandatoryFilterOutcome.None);
    public ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default) =>
        ValueTask.FromResult(false);
}

internal sealed class TestMemoryConsentCache : IConsentCacheService
{
    private readonly Dictionary<string, TableAccessDecision> _cache = new(StringComparer.Ordinal);
    private static string Key(TenantId t, Sid s, TableIdentifier id, string? ctx) => $"{t.Value}|{s.Value}|{id}|{ctx}";

    public Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default) =>
        Task.FromResult(_cache.TryGetValue(Key(tenant, userSid, table, contextHash), out var d) ? d : null);

    public Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(TenantId tenant, Sid userSid, IReadOnlyList<TableIdentifier> tables, string? contextHash = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>>(
            tables.ToDictionary(t => t, t => _cache.TryGetValue(Key(tenant, userSid, t, contextHash), out var d) ? d : null));

    public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
    {
        _cache[Key(tenant, userSid, table, contextHash)] = decision;
        return Task.CompletedTask;
    }

    public Task ClearL1CacheAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class TestNoOpConsentCache : IConsentCacheService
{
    public Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default) =>
        Task.FromResult<TableAccessDecision?>(null);
    public Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(TenantId tenant, Sid userSid, IReadOnlyList<TableIdentifier> tables, string? contextHash = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>>(tables.ToDictionary(t => t, _ => (TableAccessDecision?)null));
    public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearL1CacheAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestInMemoryConsentRepository(IReadOnlyList<Consent> consents, bool filterWindow = true) : IConsentRepository
{
    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        if (!filterWindow)
        {
            return Task.FromResult(consents);
        }

        var sids = subjects.ToHashSet();
        return Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c =>
            !c.IsRevoked && c.TableIdentifier.Equals(table) &&
            c.ValidFrom <= atTime && c.EffectiveValidTo > atTime &&
            (tenantId is null || c.TenantId == tenantId.Value) &&
            (c.GranteeSid is null || sids.Contains(c.GranteeSid.Value))).ToList());
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        Task.FromResult(consents);
    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult(consents.FirstOrDefault(c => c.Id == consentId));
    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
    public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestInMemoryClassificationStore : IClassificationRepository
{
    public Dictionary<string, DataClassification> Definitions { get; }
    public Dictionary<string, ClassificationMaskRule> MaskRules { get; }
    public List<ColumnClassification> ColumnClassifications { get; } = [];
    public List<ColumnMaskOverride> ColumnMaskOverrides { get; } = [];
    public HashSet<string> ReferencedCodes { get; } = new(StringComparer.Ordinal);

    public TestInMemoryClassificationStore(string tenantId)
    {
        var builtIn = ClassificationAccessResolver.BuiltInCatalog(tenantId);
        Definitions = new Dictionary<string, DataClassification>(builtIn.Definitions, StringComparer.Ordinal);
        MaskRules = new Dictionary<string, ClassificationMaskRule>(builtIn.MaskRules, StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<DataClassification>> GetClassificationsAsync(string? scope, bool includeInactive = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DataClassification>>(Definitions.Values.Where(d => includeInactive || d.IsActive).ToList());

    public Task<DataClassification?> GetClassificationAsync(string scope, string code, CancellationToken ct = default) =>
        Task.FromResult(Definitions.TryGetValue(code, out var d) ? d : null);

    public Task<DataClassification> UpsertClassificationAsync(DataClassification classification, CancellationToken ct = default)
    {
        Definitions[classification.Code] = classification;
        return Task.FromResult(classification);
    }

    public Task<IReadOnlyList<ClassificationMaskRule>> GetClassificationMaskRulesAsync(string? scope, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClassificationMaskRule>>(MaskRules.Values.ToList());

    public Task<ClassificationMaskRule?> GetClassificationMaskRuleAsync(string scope, string code, CancellationToken ct = default) =>
        Task.FromResult(MaskRules.TryGetValue(code, out var r) ? r : null);

    public Task<ClassificationMaskRule> UpsertClassificationMaskRuleAsync(ClassificationMaskRule rule, CancellationToken ct = default)
    {
        MaskRules[rule.Code] = rule;
        return Task.FromResult(rule);
    }

    public Task<IReadOnlyList<ColumnClassification>> GetColumnClassificationsAsync(string tableColumnId, string? scope = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ColumnClassification>>(ColumnClassifications.Where(c => c.TableColumnId == tableColumnId).ToList());

    public Task AssignColumnClassificationAsync(ColumnClassification assignment, CancellationToken ct = default)
    {
        ColumnClassifications.Add(assignment);
        return Task.CompletedTask;
    }

    public Dictionary<string, TableIdentifier> ColumnTables { get; } = new(StringComparer.Ordinal);

    public Task<TableIdentifier?> GetTableIdentifierByColumnIdAsync(string tableColumnId, CancellationToken ct = default) =>
        Task.FromResult<TableIdentifier?>(ColumnTables.TryGetValue(tableColumnId, out var table) ? table : null);

    public int RemoveCalls { get; private set; }

    public Task<bool> RemoveColumnClassificationAsync(string scope, string tableColumnId, string code, CancellationToken ct = default)
    {
        RemoveCalls++;
        return Task.FromResult(ColumnClassifications.RemoveAll(c => c.Scope == scope && c.TableColumnId == tableColumnId && c.Code == code) > 0);
    }

    public Task<IReadOnlyList<ColumnMaskOverride>> GetColumnMaskOverridesAsync(string tableColumnId, string? scope = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ColumnMaskOverride>>(ColumnMaskOverrides.Where(o => o.TableColumnId == tableColumnId).ToList());

    public Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(ColumnMaskOverride maskOverride, CancellationToken ct = default)
    {
        ColumnMaskOverrides.RemoveAll(o => o.TableColumnId == maskOverride.TableColumnId && o.Scope == maskOverride.Scope && o.Code == maskOverride.Code);
        ColumnMaskOverrides.Add(maskOverride);
        return Task.FromResult(maskOverride);
    }

    public Task<bool> DeleteColumnMaskOverrideAsync(string scope, string tableColumnId, string? code, CancellationToken ct = default) =>
        Task.FromResult(ColumnMaskOverrides.RemoveAll(o => o.TableColumnId == tableColumnId && o.Scope == scope && o.Code == code) > 0);

    public Task<bool> IsClassificationReferencedAsync(string scope, string code, CancellationToken ct = default) =>
        Task.FromResult(ReferencedCodes.Contains(code) || ColumnClassifications.Any(c => c.Code == code));

}
