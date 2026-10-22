namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// The one write seam for consents (PLAN-GOV-129 A-9): GraphQL, REST admin, OpenMetadata sync and declarative apply all call
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
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await _inner.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, ct).ConfigureAwait(false);
    }

    // PLAN-GOV-158-INC1: the description seam is additive and shares the same validation of the create list.
    private IConsentDescriptionRepository InnerDescriptions =>
        _inner as IConsentDescriptionRepository ?? throw new NotSupportedException("The inner consent repository does not support consent descriptions.");

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct)
    {
        foreach (var consent in create)
        {
            await ValidateAsync(consent, ct).ConfigureAwait(false);
        }

        await InnerDescriptions.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, descriptionUpdates, ct).ConfigureAwait(false);
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

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId: null, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
        _inner.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
        _inner.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId: null, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        _inner.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId, ct);

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => _inner.GetConsentByIdAsync(consentId, ct);

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        _inner.RevokeConsentAsync(consentId, revokedBySid, reason, ct);

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default) =>
        _inner.GetExpiringConsentsAsync(threshold, ct);

    public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) =>
        _inner.ExtendConsentExpiryAsync(consentId, newValidTo, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActiveConsentsByConsentRequestIdAsync(consentRequestId, atTime, ct);

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
        _inner.RevokeSystemConsentAsync(consentId, consentRequestId, revokedBySid, reason, ct);

    public Task<IReadOnlyList<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default) =>
        _inner.GetTablesWithConsentsForTenantAsync(tenantId, ct);

    public Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(TableIdentifier table, string roleName, TenantId? tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
        _inner.GetActiveRoleConsentsAsync(table, roleName, tenantId, atTime, ct);

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => _inner.GetConsentPageAsync(query, ct);
}
