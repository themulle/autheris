namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Products;
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
    private readonly IGovernanceDomainRepository? _domainRepository;
    private readonly IEmployeeGroupLookup _employeeGroupLookup;
    private readonly IGovernanceChangeNotifier? _changeNotifier;
    private readonly TimeProvider _timeProvider;
    private readonly IDataProductRepository? _productRepository;

    public ScimGroupMembershipValidator(
        IFunctionalRightRepository? functionalRightRepository = null,
        IAuditWriter? auditWriter = null,
        double thresholdPercentage = 10.0,
        IGovernanceDomainRepository? domainRepository = null,
        IEmployeeGroupLookup? employeeGroupLookup = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null,
        IDataProductRepository? productRepository = null)
    {
        _functionalRightRepository = functionalRightRepository;
        _auditWriter = auditWriter;
        _thresholdPercentage = thresholdPercentage;
        _domainRepository = domainRepository;
        _employeeGroupLookup = employeeGroupLookup ?? EmptyEmployeeGroupLookup.Instance;
        _changeNotifier = changeNotifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _productRepository = productRepository;
    }

    public Task ValidateMembershipChangeAsync(
        TenantId tenantId, string realmId, string groupId, int currentMemberCount, int membersBeingRemovedCount, CancellationToken ct = default) =>
        ValidateMembershipChangeAsync(tenantId, realmId, groupId, currentMemberCount, membersBeingRemovedCount, null, null, ct);

    public async Task ValidateMembershipChangeAsync(
        TenantId tenantId, string realmId, string groupId, int currentMemberCount, int membersBeingRemovedCount,
        string? groupObjectSid, string? previousGroupObjectSid = null, CancellationToken ct = default)
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

        // Check if group is privileged (role-mapped right, owner group, or employee group)
        var groupKeys = PrivilegedGroupLookupHelper.BuildCandidateKeys(realmId, groupId, groupObjectSid, previousGroupObjectSid);

        bool isPrivileged = false;
        if (_functionalRightRepository != null)
        {
            var mappings = await _functionalRightRepository.GetApprovedMappingsForGroupsAndRolesAsync(
                tenantId, realmId, groupKeys.ToArray(), ct).ConfigureAwait(false);

            foreach (var m in mappings)
            {
                if (m.FunctionalRight.IsPrivileged)
                {
                    isPrivileged = true;
                    break;
                }
            }
        }

        if (!isPrivileged && _domainRepository != null)
        {
            var domainMatches = await PrivilegedGroupLookupHelper.FindMatchingDomainOwnerGroupSidsAsync(
                tenantId, groupKeys, _domainRepository, stopOnFirst: true, ct).ConfigureAwait(false);
            isPrivileged = domainMatches.Count > 0;
        }

        if (!isPrivileged && _productRepository != null)
        {
            var productMatches = await PrivilegedGroupLookupHelper.FindMatchingProductOwnerGroupSidsAsync(
                tenantId, groupKeys, _productRepository, stopOnFirst: true, ct).ConfigureAwait(false);
            isPrivileged = productMatches.Count > 0;
        }

        if (!isPrivileged)
        {
            var employeeMatches = await PrivilegedGroupLookupHelper.FindMatchingEmployeeGroupSidsAsync(
                tenantId, groupKeys, _employeeGroupLookup, configuredEmployeeGroupSids: null, stopOnFirst: true, ct).ConfigureAwait(false);
            isPrivileged = employeeMatches.Count > 0;
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

            if (_changeNotifier != null)
            {
                await _changeNotifier.NotifyAsync(
                    GovernanceChangeEvent.ForIdentity(tenantId.Value, realmId, _timeProvider.GetUtcNow(), groupId: groupId),
                    ct).ConfigureAwait(false);
            }

            throw new MassMembershipChangeQuarantineException(groupId, percentageRemoved);
        }
    }
}
