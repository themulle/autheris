namespace Autheris.Application.Governance.Rights;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Rights.Validators;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed class FunctionalRightMapper : IFunctionalRightMapper
{
    private readonly IFunctionalRightRepository _repository;
    private readonly IIdentityRealmRegistry _realmRegistry;
    private readonly IAuditWriter? _auditWriter;
    private readonly TimeProvider _timeProvider;

    public FunctionalRightMapper(
        IFunctionalRightRepository repository,
        IIdentityRealmRegistry realmRegistry,
        IAuditWriter? auditWriter = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _realmRegistry = realmRegistry ?? throw new ArgumentNullException(nameof(realmRegistry));
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlySet<FunctionalRight>> GetRightsForCallerAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        string realmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var groupsAndRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Canonical effective group SIDs (pipeline issuer only); raw token group claims are never read here.
        foreach (var sid in principal.GetGroupSids())
        {
            groupsAndRoles.Add(sid.Value);
        }

        // Add roles
        foreach (var role in principal.GetUserRoles())
        {
            groupsAndRoles.Add(role);
        }

        var rights = await GetRightsForGroupsAndRolesAsync(tenantId, realmId, groupsAndRoles, ct).ConfigureAwait(false);
        // NL-SC-01: a GatewayRole claim never implies a right; roles reach rights only through configured, approved assignments.
        return new HashSet<FunctionalRight>(rights);
    }

    public async Task<IReadOnlySet<FunctionalRight>> GetRightsForGroupsAndRolesAsync(
        TenantId tenantId,
        string realmId,
        IReadOnlyCollection<string> groupsAndRoles,
        CancellationToken ct = default)
    {
        if (groupsAndRoles == null || groupsAndRoles.Count == 0)
        {
            return new HashSet<FunctionalRight>();
        }

        var realm = _realmRegistry.GetRealm(realmId);

        var mappings = await _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, groupsAndRoles, ct).ConfigureAwait(false);

        var rights = new HashSet<FunctionalRight>();
        foreach (var mapping in mappings)
        {
            // Single approval: a saved mapping is approved; a row without an approver never confers a right (fail closed).
            if (string.IsNullOrWhiteSpace(mapping.ApprovedBy))
            {
                continue;
            }

            // A mapping never confers an IT or governance capacity (any realm, customer realms included) and never a role outside the
            // realm's allowed gateway roles: a legacy row that does is ignored.
            if (FunctionalRightMappingValidator.ConfersCapacity(mapping.FunctionalRight) ||
                !FunctionalRightMappingValidator.WithinRealmRoles(mapping.FunctionalRight, realm))
            {
                continue;
            }

            rights.Add(mapping.FunctionalRight);
        }

        return rights;
    }

    public async Task<bool> HasRightAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        string realmId,
        FunctionalRight right,
        CancellationToken ct = default)
    {
        var rights = await GetRightsForCallerAsync(principal, tenantId, realmId, ct).ConfigureAwait(false);
        return rights.Contains(right);
    }

    public async Task<FunctionalRightMapping> SaveMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        string recertClass,
        SingleApprovalWriter writer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(writer.SubjectKey);
        var now = _timeProvider.GetUtcNow();

        // Single approval (Amendment 2): the save of a governance admin is the approval, so the mapping is effective immediately.
        var mapping = new FunctionalRightMapping(
            tenantId,
            realmId,
            groupOrRole,
            right,
            ApprovedBy: writer.SubjectKey,
            ApprovedAt: now,
            RecertClass: string.IsNullOrWhiteSpace(recertClass) ? "Standard" : recertClass,
            CreatedBy: writer.SubjectKey,
            CreatedAt: now);

        FunctionalRightMappingValidator.ValidateCreation(mapping, _realmRegistry.GetRealm(realmId));

        // Compensating control: a writer never maps a right to an own identifier, group or role.
        SelfWideningGuard.EnsureDoesNotWidenOwnAccess(writer, [groupOrRole]);

        var before = await _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
        await RecordChangeAsync(writer, tenantId, "SECURITY_FUNCTIONAL_MAPPING_SAVED", HashOf(before), HashOf(mapping), ct).ConfigureAwait(false);
        await _repository.CreateMappingAsync(mapping, ct).ConfigureAwait(false);
        return mapping;
    }

    public async Task DeleteMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        SingleApprovalWriter writer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(writer.SubjectKey);
        var before = await _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
        await RecordChangeAsync(writer, tenantId, "SECURITY_FUNCTIONAL_MAPPING_DELETED", HashOf(before), null, ct).ConfigureAwait(false);
        await _repository.DeleteMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
    }

    // Compensating control: every change is audited with a before/after hash (never values) before it is written; without an audit writer, or
    // when the audit fails, the change is refused (fail closed).
    private async Task RecordChangeAsync(SingleApprovalWriter writer, TenantId tenantId, string eventType, string? beforeHash, string? afterHash, CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            throw new SingleApprovalRefusedException("AuditFailed", "The functional-right change was refused: no audit writer is available.");
        }

        var entry = SelfWideningGuard.AuditEntry(writer, new SingleApprovalChange(tenantId, eventType, "FUNCTIONAL_RIGHT_MAPPINGS", beforeHash, afterHash));
        try
        {
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SingleApprovalRefusedException("AuditFailed", "The functional-right change was refused: the audit entry could not be written.", ex);
        }
    }

    private static string? HashOf(FunctionalRightMapping? mapping)
    {
        if (mapping is null)
        {
            return null;
        }

        var canonical = string.Join('\n', mapping.TenantId.Value, mapping.RealmId, mapping.GroupOrRole, mapping.FunctionalRight.Value,
            mapping.ApprovedBy ?? string.Empty, mapping.RecertClass);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
