namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

/// <summary>Startup summary of the workload subject mappings: counts only (never a subject, a key or a file content), and a warning for every ClusterAdmin mapping.</summary>
public static class SubjectRoleStartupLog
{
    public static void Write(ILogger logger, IEnumerable<IdentityRealm> realms)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(realms);

        foreach (var realm in realms)
        {
            if (realm.SubjectRoles is not { Count: > 0 } mapping)
            {
                continue;
            }

            var roleCount = 0;
            var privileged = 0;
            foreach (var roles in mapping.Values)
            {
                roleCount += roles.Count;
                if (roles.Contains(GatewayRole.ClusterAdmin))
                {
                    privileged++;
                }
            }

            logger.LogInformation(
                "Realm {RealmId} maps {EntryCount} workload subjects to {RoleCount} gateway roles ({PrivilegedCount} privileged entries).",
                realm.Id, mapping.Count, roleCount, privileged);
            if (privileged > 0)
            {
                logger.LogWarning(
                    "Realm {RealmId} maps {PrivilegedCount} workload subjects to ClusterAdmin; review the realm's SubjectRoles configuration.",
                    realm.Id, privileged);
            }
        }
    }
}
