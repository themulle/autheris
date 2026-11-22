namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Resolves configured employee/internal group SIDs for a tenant so privileged group audits can flag changes to employee-access groups.
/// </summary>
public interface IEmployeeGroupLookup
{
    IReadOnlyCollection<string> GetEmployeeGroupSids(TenantId tenantId);
}

/// <summary>
/// Default empty <see cref="IEmployeeGroupLookup"/> used when no employee group list is configured.
/// </summary>
public sealed class EmptyEmployeeGroupLookup : IEmployeeGroupLookup
{
    public static readonly EmptyEmployeeGroupLookup Instance = new();

    public IReadOnlyCollection<string> GetEmployeeGroupSids(TenantId tenantId) => [];
}

public sealed record PrivilegedGroupClassification(
    bool IsPrivileged,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> MatchedIdentifiers,
    IReadOnlyList<string> MatchedRights);

public sealed record ScimMembershipAuditItem(string MemberKey, string MemberType, string Operation);

/// <summary>
/// Classifies SCIM groups against tenant owner groups, configured employee groups, and role-mapped groups,
/// enforcing fail-closed audit logging prior to mutation and dispatching notifications after commit.
/// </summary>
public sealed class PrivilegedGroupChangeAuditor
{
    private const int MaxDomainPages = 200;

    private readonly IGovernanceDomainRepository? _domainRepository;
    private readonly IFunctionalRightRepository? _functionalRightRepository;
    private readonly IEmployeeGroupLookup _employeeGroupLookup;
    private readonly IAuditWriter? _auditWriter;
    private readonly IGovernanceNotificationDispatcher _notificationDispatcher;
    private readonly HashSet<string> _configuredEmployeeGroupSids;

    public PrivilegedGroupChangeAuditor(
        IGovernanceDomainRepository? domainRepository = null,
        IFunctionalRightRepository? functionalRightRepository = null,
        IEmployeeGroupLookup? employeeGroupLookup = null,
        IAuditWriter? auditWriter = null,
        IGovernanceNotificationDispatcher? notificationDispatcher = null,
        IEnumerable<string>? configuredEmployeeGroupSids = null)
    {
        _domainRepository = domainRepository;
        _functionalRightRepository = functionalRightRepository;
        _employeeGroupLookup = employeeGroupLookup ?? EmptyEmployeeGroupLookup.Instance;
        _auditWriter = auditWriter;
        _notificationDispatcher = notificationDispatcher ?? NoOpGovernanceNotificationDispatcher.Instance;
        _configuredEmployeeGroupSids = configuredEmployeeGroupSids?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PrivilegedGroupClassification> ClassifyGroupAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string? groupObjectSid = null,
        string? previousGroupObjectSid = null,
        CancellationToken ct = default)
    {
        var candidates = BuildCandidateKeys(realmId, groupId, groupObjectSid, previousGroupObjectSid);
        if (candidates.Count == 0)
        {
            return new PrivilegedGroupClassification(false, [], [], []);
        }

        var categories = new HashSet<string>(StringComparer.Ordinal);
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedRights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_domainRepository is not null)
        {
            var offset = 0;
            for (var pageIndex = 0; pageIndex < MaxDomainPages; pageIndex++)
            {
                var domains = await _domainRepository.ListAsync(
                    tenantId, IGovernanceDomainRepository.MaxPageSize, offset, ct).ConfigureAwait(false);
                if (domains.Count == 0)
                {
                    break;
                }

                foreach (var domain in domains)
                {
                    if (domain.OwnerGroupSid is { Value: var ownerSid } && candidates.Contains(ownerSid))
                    {
                        categories.Add("tenant_owner_group");
                        matchedIds.Add(ownerSid);
                    }
                }

                if (domains.Count < IGovernanceDomainRepository.MaxPageSize)
                {
                    break;
                }

                offset += domains.Count;
            }
        }

        foreach (var employeeSid in _employeeGroupLookup.GetEmployeeGroupSids(tenantId))
        {
            if (!string.IsNullOrWhiteSpace(employeeSid) && candidates.Contains(employeeSid.Trim()))
            {
                categories.Add("employee_group");
                matchedIds.Add(employeeSid.Trim());
            }
        }

        foreach (var employeeSid in _configuredEmployeeGroupSids)
        {
            if (candidates.Contains(employeeSid))
            {
                categories.Add("employee_group");
                matchedIds.Add(employeeSid);
            }
        }

        if (_functionalRightRepository is not null)
        {
            var mappings = await _functionalRightRepository.GetMappingsAsync(tenantId, realmId, ct).ConfigureAwait(false);
            foreach (var mapping in mappings)
            {
                if (candidates.Contains(mapping.GroupOrRole))
                {
                    categories.Add("role_mapped_group");
                    matchedIds.Add(mapping.GroupOrRole);
                    matchedRights.Add(mapping.FunctionalRight.Value);
                }
            }

            if (mappings.Count == 0)
            {
                var approved = await _functionalRightRepository.GetApprovedMappingsForGroupsAndRolesAsync(
                    tenantId, realmId, candidates.ToArray(), ct).ConfigureAwait(false);
                foreach (var mapping in approved)
                {
                    if (candidates.Contains(mapping.GroupOrRole))
                    {
                        categories.Add("role_mapped_group");
                        matchedIds.Add(mapping.GroupOrRole);
                        matchedRights.Add(mapping.FunctionalRight.Value);
                    }
                }
            }
        }

        return new PrivilegedGroupClassification(
            categories.Count > 0,
            categories.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            matchedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            matchedRights.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task AuditBeforeMutationAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string? groupObjectSid,
        IReadOnlyList<ScimMembershipAuditItem> changes,
        PrivilegedGroupClassification classification,
        string operation,
        DateTimeOffset occurredAt,
        CancellationToken ct = default)
    {
        if (!classification.IsPrivileged || _auditWriter is null)
        {
            return;
        }

        var entry = new AuditLogEntry
        {
            TenantId = tenantId,
            OccurredAt = occurredAt,
            EventType = AuditEventTypes.PrivilegedGroupMembershipChanged,
            ActorSid = new Sid($"scim-client:{realmId}"),
            TargetTable = "IDENTITY_GROUP_MEMBERS",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                realm_id = realmId,
                group_id = groupId,
                group_sid = groupObjectSid,
                operation,
                categories = classification.Categories,
                matched_identifiers = classification.MatchedIdentifiers,
                matched_rights = classification.MatchedRights,
                changes = changes.Select(c => new
                {
                    member_key = c.MemberKey,
                    member_type = c.MemberType,
                    operation = c.Operation
                })
            })
        };

        await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
    }

    public Task DispatchNotificationAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string operation,
        PrivilegedGroupClassification classification,
        DateTimeOffset occurredAt,
        CancellationToken ct = default)
    {
        if (!classification.IsPrivileged)
        {
            return Task.CompletedTask;
        }

        var summary = $"{string.Join(',', classification.Categories)}:{operation}";
        var message = new GovernanceNotificationMessage(
            AuditEventTypes.PrivilegedGroupMembershipChanged,
            tenantId.Value,
            realmId,
            groupId,
            summary,
            occurredAt);
        return _notificationDispatcher.DispatchAsync(message, ct);
    }

    private static HashSet<string> BuildCandidateKeys(
        string realmId,
        string groupId,
        string? groupObjectSid,
        string? previousGroupObjectSid)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            keys.Add(groupId.Trim());
            if (!string.IsNullOrWhiteSpace(realmId) && SidFormat.TryFormatRealmGroupKey(realmId, groupId, out var realmKey))
            {
                keys.Add(realmKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(groupObjectSid))
        {
            keys.Add(groupObjectSid.Trim());
        }

        if (!string.IsNullOrWhiteSpace(previousGroupObjectSid))
        {
            keys.Add(previousGroupObjectSid.Trim());
        }

        return keys;
    }
}
