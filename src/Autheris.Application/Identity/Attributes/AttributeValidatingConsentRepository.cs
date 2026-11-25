namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// The one write seam for consents (129 A-9): GraphQL, REST admin, OpenMetadata sync and declarative apply all call
/// <see cref="CreateConsentAsync"/> or <see cref="ReplaceConsentsAsync"/> on <see cref="IConsentRepository"/>; this decorator checks the
/// registry rules before the inner repository stores anything. Everything else is forwarded unchanged.
/// </summary>
public sealed class AttributeValidatingConsentRepository : IConsentRepository, IConsentDescriptionRepository
{
    private readonly IConsentRepository _inner;
    private readonly IConsentMaterializationGuard _guard;

    public AttributeValidatingConsentRepository(
        IConsentRepository inner, ICallerAttributeRegistry registry, ITableMetadataRepository tables, IOptions<GatewayOptions> options)
        : this(inner, new CallerAttributeConsentGuard(
            () => registry ?? throw new ArgumentNullException(nameof(registry)),
            () => tables ?? throw new ArgumentNullException(nameof(tables)),
            options))
    {
    }

    /// <summary>GOV-12: the decorator delegates to the shared materialisation guard (single implementation of the checks).</summary>
    public AttributeValidatingConsentRepository(IConsentRepository inner, IConsentMaterializationGuard guard)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        await ValidateAsync(consent, ct).ConfigureAwait(false);
        return await _inner.CreateConsentAsync(consent, ct).ConfigureAwait(false);
    }

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await _inner.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, ct).ConfigureAwait(false);
    }

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlySet<Guid>? prunedConsentIds,
        string? pruneReason = null,
        CancellationToken ct = default)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await _inner.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, prunedConsentIds, pruneReason, ct).ConfigureAwait(false);
    }

    // the description seam is additive and shares the same validation of the create list.
    private IConsentDescriptionRepository InnerDescriptions =>
        _inner as IConsentDescriptionRepository ?? throw new NotSupportedException("The inner consent repository does not support consent descriptions.");

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await InnerDescriptions.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, descriptionUpdates, ct).ConfigureAwait(false);
    }

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
        IReadOnlySet<Guid>? prunedConsentIds,
        string? pruneReason = null,
        CancellationToken ct = default)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await InnerDescriptions.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, descriptionUpdates, prunedConsentIds, pruneReason, ct).ConfigureAwait(false);
    }

    public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default) =>
        InnerDescriptions.UpdateConsentDescriptionWithAuditAsync(consentId, tenant, description, audit, ct);

    public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default) =>
        InnerDescriptions.LoadDescriptionsAsync(consents, ct);

    private async Task ValidateAsync(Consent consent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        await _guard.EnsureMayMaterializeAsync(consent.TenantId, consent.TableIdentifier, consent.RowFilters, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
        _inner.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId, ct);

    public Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>>> GetActiveConsentsForSubjectsBatchAsync(
        IEnumerable<Sid> subjects, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
        _inner.GetActiveConsentsForSubjectsBatchAsync(subjects, tables, atTime, tenantId, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        _inner.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentHeadersForSubjectsAsync(IEnumerable<Sid> subjects, DateTimeOffset? atTime, TenantId tenantId, CancellationToken ct = default) =>
        _inner.GetAllActiveConsentHeadersForSubjectsAsync(subjects, atTime, tenantId, ct);

    public Task<DateTimeOffset?> GetNextScheduledAllowAsync(IEnumerable<Sid> subjects, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct = default) =>
        _inner.GetNextScheduledAllowAsync(subjects, atTime, tenantId, ct);

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => _inner.GetConsentByIdAsync(consentId, ct);

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) =>
        _inner.RevokeConsentAsync(consentId, revokedBySid, reason, authority, ct);

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) =>
        _inner.GetExpiringConsentsAsync(threshold, maxResults, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActiveConsentsByConsentRequestIdAsync(consentRequestId, atTime, ct);

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        _inner.RevokeSystemConsentAsync(consentId, consentRequestId, revokedBySid, reason, ct);

    public Task<bool> RevokeSystemConsentByIdAsync(TenantId tenantId, Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        _inner.RevokeSystemConsentByIdAsync(tenantId, consentId, revokedBySid, reason, ct);

    public Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, int maxResults, CancellationToken ct = default) =>
        _inner.GetActiveGrantsForSubjectsAsync(tenantId, subjects, atTime, maxResults, ct);

    public Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAfterAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, Guid? afterId, int maxResults, CancellationToken ct = default) =>
        _inner.GetActiveGrantsForSubjectsAfterAsync(tenantId, subjects, atTime, afterId, maxResults, ct);

    public Task<IReadOnlyList<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default) =>
        _inner.GetTablesWithConsentsForTenantAsync(tenantId, ct);

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => _inner.GetConsentPageAsync(query, ct);

    public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActivePatternConsentsAsync(tenantId, atTime, ct);

    public Task<IReadOnlyList<Consent>> GetPatternConsentsWithPendingDeniesAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetPatternConsentsWithPendingDeniesAsync(tenantId, atTime, ct);

    public Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActiveInternalPatternConsentsAsync(atTime, ct);

    // The interface defaults of the two reads below return nothing; not forwarding them silently drops every unmask grant (#1009 A-3)
    // and every recently expired consent of the registered repository.
    public Task<IReadOnlyList<Consent>> GetActiveUnmaskGrantsForSubjectsAsync(
        IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct = default) =>
        _inner.GetActiveUnmaskGrantsForSubjectsAsync(subjects, table, atTime, tenantId, ct);

    public Task<IReadOnlySet<TableIdentifier>> GetRecentlyExpiredTableIdsAsync(
        Sid userSid, TenantId tenantId, DateTimeOffset since, CancellationToken ct = default) =>
        _inner.GetRecentlyExpiredTableIdsAsync(userSid, tenantId, since, ct);
}
