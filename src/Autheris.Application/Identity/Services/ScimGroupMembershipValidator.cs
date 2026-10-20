namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class MassMembershipChangeQuarantineException : Exception
{
    public double RemovedPercentage { get; }
    public string GroupId { get; }

    public MassMembershipChangeQuarantineException(string groupId, double removedPercentage)
        : base($"Mass membership removal ({removedPercentage:F1}%) on privileged group '{groupId}' exceeds threshold and is quarantined.")
    {
        GroupId = groupId;
        RemovedPercentage = removedPercentage;
    }
}

public sealed class ScimGroupMembershipValidator
{
    private readonly IFunctionalRightRepository? _functionalRightRepository;
    private readonly IAuditWriter? _auditWriter;
    private readonly double _thresholdPercentage;

    public ScimGroupMembershipValidator(
        IFunctionalRightRepository? functionalRightRepository = null,
        IAuditWriter? auditWriter = null,
        double thresholdPercentage = 10.0)
    {
        _functionalRightRepository = functionalRightRepository;
        _auditWriter = auditWriter;
        _thresholdPercentage = thresholdPercentage;
    }

    public async Task ValidateMembershipChangeAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        int currentMemberCount,
        int membersBeingRemovedCount,
        CancellationToken ct = default)
    {
        if (currentMemberCount == 0 || membersBeingRemovedCount == 0)
        {
            return;
        }

        double percentageRemoved = (double)membersBeingRemovedCount / currentMemberCount * 100.0;
        if (percentageRemoved <= _thresholdPercentage)
        {
            return;
        }

        // Check if group is mapped to any privileged right
        if (_functionalRightRepository != null)
        {
            var mappings = await _functionalRightRepository.GetApprovedMappingsForGroupsAndRolesAsync(
                tenantId, realmId, [groupId], ct).ConfigureAwait(false);

            bool isPrivileged = false;
            foreach (var m in mappings)
            {
                if (m.FunctionalRight.IsPrivileged)
                {
                    isPrivileged = true;
                    break;
                }
            }

            if (isPrivileged)
            {
                // Emit Tier-A audit event
                if (_auditWriter != null)
                {
                    var entry = new AuditLogEntry
                    {
                        TenantId = tenantId,
                        EventType = "SECURITY_MASS_MEMBERSHIP_REVOCATION_BLOCKED",
                        ActorSid = new Sid("SCIM_PUSH"),
                        TargetTable = "IDENTITY_GROUP_MEMBERS",
                        Decision = "DENY",
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            realm_id = realmId,
                            group_id = groupId,
                            current_members = currentMemberCount,
                            removed_members = membersBeingRemovedCount,
                            percentage_removed = percentageRemoved,
                            threshold_percentage = _thresholdPercentage,
                            reason = "mass_membership_change_quarantined"
                        })
                    };

                    await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
                }

                throw new MassMembershipChangeQuarantineException(groupId, percentageRemoved);
            }
        }
    }
}
