namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public interface IGroupDenyInspector
{
    ValueTask<bool> AnyGroupCarriesDenyAsync(TenantId tenantId, IReadOnlySet<Sid> groupSids, CancellationToken ct = default);
}

public sealed class GovernanceGroupDenyInspector(
    IConsentRepository? consentRepository = null,
    IVirtualFilterRepository? virtualFilterRepository = null,
    ILogger<GovernanceGroupDenyInspector>? logger = null,
    TimeProvider? timeProvider = null) : IGroupDenyInspector
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<bool> AnyGroupCarriesDenyAsync(
        TenantId tenantId,
        IReadOnlySet<Sid> groupSids,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groupSids);

        if (groupSids.Count == 0)
        {
            return false;
        }

        if (consentRepository is null || virtualFilterRepository is null)
        {
            // Fail closed: cannot verify no group carries a deny policy.
            return true;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            Guid? afterId = null;

            while (true)
            {
                var page = await consentRepository.GetConsentPageAsync(
                    new ConsentPageQuery(tenantId, GranteeType: GranteeType.Group, IncludeInactive: false, AfterId: afterId, PageSize: 200),
                    ct).ConfigureAwait(false);

                foreach (var consent in page.Items)
                {
                    if (!consent.IsActive(now)
                        || consent.GranteeType != GranteeType.Group
                        || consent.GranteeSid is not { } consentSid
                        || !groupSids.Contains(consentSid))
                    {
                        continue;
                    }

                    if (consent.Effect == ConsentEffect.Deny || consent.ColumnRules.Any(r => r.AccessLevel == ColumnAccessLevel.Deny))
                    {
                        return true;
                    }
                }

                if (page.NextAfterId is null)
                {
                    break;
                }

                afterId = page.NextAfterId;
            }

            var snapshot = await virtualFilterRepository.LoadSnapshotAsync(ct).ConfigureAwait(false);
            foreach (var profile in snapshot.Profiles)
            {
                if (profile.TenantId != tenantId
                    || profile.Status != FilterApprovalStatus.Active
                    || profile.PendingDeletion
                    || profile.GranteeType != GranteeType.Group
                    || profile.GranteeSid is not { } sid
                    || !groupSids.Contains(sid))
                {
                    continue;
                }

                if (profile.Uncovered == UncoveredPolicy.Deny || profile.Bindings.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to inspect group deny policies for tenant {TenantId}; failing closed.", tenantId.Value);
            return true;
        }
    }
}
