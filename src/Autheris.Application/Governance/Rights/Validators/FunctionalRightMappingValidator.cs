namespace Autheris.Application.Governance.Rights.Validators;

using System;
using System.Security;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

/// <summary>
/// A functional-right mapping takes effect on save (single approval), so what it may confer is bounded instead: never an
/// IT or governance capacity, and never a role outside the realm's <c>AllowedGatewayRoles</c>.
/// </summary>
public static class FunctionalRightMappingValidator
{
    public static void ValidateCreation(FunctionalRightMapping mapping, IdentityRealm? realm)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.GroupOrRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.CreatedBy);

        if (ConfersCapacity(mapping.FunctionalRight))
        {
            throw new SecurityException(
                $"A functional-right mapping cannot confer the IT or governance capacity '{mapping.FunctionalRight.Value}'.");
        }

        if (!WithinRealmRoles(mapping.FunctionalRight, realm))
        {
            throw new SecurityException(
                $"A functional-right mapping cannot confer the role '{mapping.FunctionalRight.Value}' outside the realm's allowed gateway roles.");
        }
    }

    /// <summary>
    /// True for the rights that are an IT or governance capacity (the privileged rights, TenantAdmin and GovernanceAdmin): they come
    /// only from the identity configuration, never from a mapping, in every realm (this also covers the customer-realm ceiling).
    /// </summary>
    public static bool ConfersCapacity(FunctionalRight right) =>
        right.IsPrivileged ||
        right == FunctionalRight.TenantAdmin ||
        right == FunctionalRight.GovernanceAdmin ||
        right == FunctionalRight.ClusterAdmin;

    /// <summary>
    /// A right that names a gateway role is conferred only when the realm is known and lists that role in its allowed gateway roles
    /// (fail closed for an unknown realm); a right that names no role is not bounded by the realm.
    /// </summary>
    public static bool WithinRealmRoles(FunctionalRight right, IdentityRealm? realm)
    {
        if (!GatewayRoleExtensions.TryParseGlobalRole(right.Value, out var role))
        {
            return true;
        }

        return realm is not null && realm.AllowedGatewayRoles.Contains(role);
    }
}
