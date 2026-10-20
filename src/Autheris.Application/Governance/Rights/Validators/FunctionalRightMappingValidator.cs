namespace Autheris.Application.Governance.Rights.Validators;

using System;
using System.ComponentModel.DataAnnotations;
using System.Security;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;

public static class FunctionalRightMappingValidator
{
    public static void ValidateCreation(FunctionalRightMapping mapping, IdentityRealm? realm)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.GroupOrRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.CreatedBy);

        // SEC-SC-22: Customer Realm Ceiling
        if (realm != null && (realm.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C or IdentityRealmKind.Customer))
        {
            if (mapping.FunctionalRight.IsPrivileged ||
                mapping.FunctionalRight == FunctionalRight.TenantAdmin ||
                mapping.FunctionalRight == FunctionalRight.GovernanceAdmin ||
                mapping.FunctionalRight == FunctionalRight.ClusterAdmin)
            {
                throw new SecurityException(
                    $"Customer Realm '{realm.Id}' cannot be mapped to administrative or privileged right '{mapping.FunctionalRight.Value}' (SEC-SC-22: customer_realm_privilege_escalation_prohibited).");
            }
        }
    }

    public static void ValidateApproval(FunctionalRightMapping mapping, string approverId)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(approverId);

        // SEC-SC-22: Four-Eyes Principle: approver != creator for privileged rights
        if (mapping.FunctionalRight.IsPrivileged)
        {
            if (string.Equals(approverId.Trim(), mapping.CreatedBy.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    $"Self-approval prohibited: Approver '{approverId}' cannot be identical to Creator '{mapping.CreatedBy}' for privileged right '{mapping.FunctionalRight.Value}' (SEC-SC-22).");
            }
        }
    }
}
