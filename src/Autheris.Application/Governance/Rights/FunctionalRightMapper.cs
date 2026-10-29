namespace Autheris.Application.Governance.Rights;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Rights.Validators;
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

    public FunctionalRightMapper(
        IFunctionalRightRepository repository,
        IIdentityRealmRegistry realmRegistry,
        IAuditWriter? auditWriter = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _realmRegistry = realmRegistry ?? throw new ArgumentNullException(nameof(realmRegistry));
        _auditWriter = auditWriter;
    }

    public async Task<IReadOnlySet<FunctionalRight>> GetRightsForCallerAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        string realmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var groupsAndRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add groups
        foreach (var sid in principal.GetGroupSids())
        {
            groupsAndRoles.Add(sid.Value);
        }

        // Add roles
        foreach (var role in principal.GetUserRoles())
        {
            groupsAndRoles.Add(role);
        }

        // Add claims with type "group", "groups", "roles"
        foreach (var c in principal.Claims)
        {
            if (string.Equals(c.Type, "group", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "groups", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, ClaimTypes.GroupSid, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(c.Value))
                {
                    groupsAndRoles.Add(c.Value.Trim());
                }
            }
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
        bool isCustomerRealm = realm != null && (realm.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C or IdentityRealmKind.Customer);

        var mappings = await _repository.GetApprovedMappingsForGroupsAndRolesAsync(
            tenantId, realmId, groupsAndRoles, ct).ConfigureAwait(false);

        var rights = new HashSet<FunctionalRight>();
        foreach (var mapping in mappings)
        {
            // SEC-SC-22: Four-Eyes Principle: privileged rights require approved_by != created_by (both non-null)
            if (mapping.FunctionalRight.IsPrivileged)
            {
                if (string.IsNullOrWhiteSpace(mapping.ApprovedBy) ||
                    string.Equals(mapping.ApprovedBy.Trim(), mapping.CreatedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // Unapproved or self-approved: fails closed (ignored)
                    continue;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(mapping.ApprovedBy))
                {
                    // Non-privileged also requires approved status
                    continue;
                }
            }

            // SEC-SC-22: Customer Realm Ceiling: barred from admin, approve, and unmask rights
            if (isCustomerRealm)
            {
                if (mapping.FunctionalRight.IsPrivileged ||
                    mapping.FunctionalRight == FunctionalRight.TenantAdmin ||
                    mapping.FunctionalRight == FunctionalRight.GovernanceAdmin ||
                    mapping.FunctionalRight == FunctionalRight.ClusterAdmin)
                {
                    continue;
                }
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

    public async Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var realm = _realmRegistry.GetRealm(mapping.RealmId);

        FunctionalRightMappingValidator.ValidateCreation(mapping, realm);

        // SEC-SC-22: If Privileged and creator attempts self-approval in the creation payload, clear approval
        var sanitized = mapping;
        if (mapping.FunctionalRight.IsPrivileged &&
            !string.IsNullOrWhiteSpace(mapping.ApprovedBy) &&
            string.Equals(mapping.ApprovedBy.Trim(), mapping.CreatedBy.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            sanitized = mapping with { ApprovedBy = null, ApprovedAt = null };
        }

        await _repository.CreateMappingAsync(sanitized, ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = mapping.TenantId,
                EventType = "SECURITY_FUNCTIONAL_MAPPING_CREATED",
                ActorSid = new Sid(mapping.CreatedBy),
                TargetTable = "FUNCTIONAL_RIGHT_MAPPINGS",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    realm_id = mapping.RealmId,
                    group_or_role = mapping.GroupOrRole,
                    functional_right = mapping.FunctionalRight.Value,
                    is_privileged = mapping.FunctionalRight.IsPrivileged,
                    approved_by = sanitized.ApprovedBy
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public async Task ApproveMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        string approverId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approverId);

        var existing = await _repository.GetMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Mapping for '{groupOrRole}' -> '{right.Value}' not found.");
        }

        FunctionalRightMappingValidator.ValidateApproval(existing, approverId);

        await _repository.ApproveMappingAsync(tenantId, realmId, groupOrRole, right, approverId, ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_FUNCTIONAL_MAPPING_APPROVED",
                ActorSid = new Sid(approverId),
                TargetTable = "FUNCTIONAL_RIGHT_MAPPINGS",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    realm_id = realmId,
                    group_or_role = groupOrRole,
                    functional_right = right.Value,
                    approved_by = approverId,
                    created_by = existing.CreatedBy
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public async Task<FunctionalRightMapping> ProposeMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        RecertClass recertClass,
        string createdBy,
        CancellationToken ct = default)
    {
        var mapping = new FunctionalRightMapping(
            tenantId,
            realmId,
            groupOrRole,
            right,
            ApprovedBy: null,
            ApprovedAt: null,
            RecertClass: recertClass.ToString(),
            CreatedBy: createdBy,
            CreatedAt: DateTimeOffset.UtcNow);

        await CreateMappingAsync(mapping, ct).ConfigureAwait(false);
        return mapping;
    }

    public async Task DeleteMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        string deletedBy,
        CancellationToken ct = default)
    {
        await _repository.DeleteMappingAsync(tenantId, realmId, groupOrRole, right, ct).ConfigureAwait(false);

        if (_auditWriter != null)
        {
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "SECURITY_FUNCTIONAL_MAPPING_DELETED",
                ActorSid = new Sid(deletedBy),
                TargetTable = "FUNCTIONAL_RIGHT_MAPPINGS",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    realm_id = realmId,
                    group_or_role = groupOrRole,
                    functional_right = right.Value,
                    deleted_by = deletedBy
                })
            };
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
    }
}

