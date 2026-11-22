using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Identity;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Policy;

public sealed partial class TableAccessPolicy
{
    private const string ProtectionUnavailableCode = "PROTECTION_UNAVAILABLE";
    private const string ProtectionClassGrantRequiredCode = "PROTECTION_CLASS_GRANT_REQUIRED";
    private const string ProtectionBaselineAllowCode = "PROTECTION_BASELINE_ALLOW";
    private const string ProtectionGrantSatisfiedCode = "PROTECTION_GRANT_SATISFIED";

    private async ValueTask<(EffectiveProtection? Protection, TableAccessDecision? Failure)> ResolveProtectionSnapshotAsync(
        TableAccessQuery query,
        bool required,
        CancellationToken ct)
    {
        if (query.Protection is not null)
        {
            return (query.Protection, null);
        }

        if (_protectionProvider is null)
        {
            return (null, null);
        }

        try
        {
            var resolved = await _protectionProvider.GetAsync(query.Tenant, query.Metadata.Identifier, ct).ConfigureAwait(false);
            return (resolved, null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return required
                ? (null, ProtectionUnavailableDecision(query.Metadata.Identifier))
                : (null, null);
        }
    }

    private async Task<TableAccessDecision> EvaluateProtectionAndConsentAsync(
        TableAccessQuery query,
        EffectiveProtection? resolvedProtection,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var protection = resolvedProtection ?? DefaultConfidentialProtection(query.Metadata);
        var effectiveTableClass = RollupEffectiveTableClass(protection);

        TenantAccessPolicy tenantPolicy;
        if (_accessPolicySettings is not null)
        {
            try
            {
                tenantPolicy = await _accessPolicySettings.GetEffectiveAsync(query.Tenant, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return ProtectionUnavailableDecision(table);
            }
        }
        else
        {
            tenantPolicy = TenantAccessPolicy.FromDeploymentDefaults(query.Tenant, new Autheris.Domain.Options.AccessPolicyOptions());
        }

        var callerContext = BuildSecurityPrincipalContext(query, _timeProvider);
        var isInternal = InternalAudienceResolver.IsInternal(
            callerContext,
            tenantPolicy.Internal,
            _realmRegistry,
            timeProvider: _timeProvider);
        var callerBaseline = isInternal ? ProtectionClass.Internal : ProtectionClass.Public;
        var baselineSatisfied = !effectiveTableClass.IsStricterThan(callerBaseline);

        var consentDecision = await ResolveConsentAsync(query, ct).ConfigureAwait(false);
        if (!consentDecision.IsAllowed && consentDecision.DenialKind == TableAccessDenialKind.ConsentDeny)
        {
            return consentDecision;
        }

        if (baselineSatisfied)
        {
            var columnAccess = ExtractExplicitColumnDenies(consentDecision);
            var baselineDecision = TableAccessDecision.Allowed(
                table,
                columnAccess,
                rowFilterSql: null,
                hasUnconstrainedColumnAllow: true) with
            {
                PolicyEpoch = consentDecision.PolicyEpoch,
                CatalogEpoch = consentDecision.CatalogEpoch,
                Reasons = BuildBaselineReasons(effectiveTableClass, consentDecision)
            };

            baselineDecision = await ApplyClassificationAccessAsync(baselineDecision, query, ct).ConfigureAwait(false);
            return ApplyProtectionMasks(baselineDecision, query.Metadata, protection);
        }

        if (consentDecision.IsAllowed)
        {
            var grantedDecision = await ApplyClassificationAccessAsync(consentDecision, query, ct).ConfigureAwait(false);
            grantedDecision = ApplyProtectionMasks(grantedDecision, query.Metadata, protection);
            return grantedDecision with
            {
                Reasons =
                [
                    .. grantedDecision.Reasons,
                    new DecisionReason(DecisionStep.Protection, ProtectionGrantSatisfiedCode, "Protection", effectiveTableClass.ToCanonicalName())
                ]
            };
        }

        var message = $"{ProtectionClassGrantRequiredCode}: Table '{table.ToQualifiedName()}' requires a data grant for class '{effectiveTableClass.ToCanonicalName()}' (caller baseline '{callerBaseline.ToCanonicalName()}').";
        var deniedReasons = consentDecision.DeniedReasons.Count > 0
            ? consentDecision.DeniedReasons
            : [message];
        return TableAccessDecision.Denied(table, message) with
        {
            DenialKind = consentDecision.DenialKind,
            DeniedReasons = deniedReasons,
            PolicyEpoch = consentDecision.PolicyEpoch,
            CatalogEpoch = consentDecision.CatalogEpoch,
            Reasons =
            [
                new DecisionReason(DecisionStep.Protection, ProtectionClassGrantRequiredCode, "Protection", effectiveTableClass.ToCanonicalName()),
                .. consentDecision.Reasons
            ]
        };
    }

    private static ProtectionClass RollupEffectiveTableClass(EffectiveProtection protection)
    {
        var current = protection.TableClass.NormalizeOrConfidential();
        foreach (var col in protection.Columns.Values)
        {
            current = ProtectionClassExtensions.Max(current, col.EffectiveClass.NormalizeOrConfidential());
        }

        return current;
    }

    private static EffectiveProtection DefaultConfidentialProtection(TableMetadata metadata) =>
        new(
            Container: ContainerDefaults.Unassigned.Container,
            OwnerGroupSid: null,
            TableClass: ProtectionClass.Confidential,
            DeclaredTableClass: ProtectionClass.Confidential,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.OnRequest);

    private static TableAccessDecision ProtectionUnavailableDecision(TableIdentifier table) =>
        TableAccessDecision.Denied(table, $"{ProtectionUnavailableCode}: Protection classification unavailable (fail-closed).") with
        {
            Reasons = [new DecisionReason(DecisionStep.Protection, ProtectionUnavailableCode, "Protection", null)]
        };

    private static Dictionary<string, ColumnAccessLevel> ExtractExplicitColumnDenies(TableAccessDecision consentDecision)
    {
        var result = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var reason in consentDecision.Reasons)
        {
            if (reason.Step == DecisionStep.ExplicitDeny &&
                string.Equals(reason.Code, "COLUMN_DENY", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(reason.Column))
            {
                result[reason.Column] = ColumnAccessLevel.Deny;
            }
        }

        return result;
    }

    private static IReadOnlyList<DecisionReason> BuildBaselineReasons(ProtectionClass tableClass, TableAccessDecision consentDecision)
    {
        var reasons = new List<DecisionReason>
        {
            new(DecisionStep.Protection, ProtectionBaselineAllowCode, "Protection", tableClass.ToCanonicalName())
        };

        foreach (var reason in consentDecision.Reasons)
        {
            if (reason.Step == DecisionStep.ExplicitDeny)
            {
                reasons.Add(reason);
            }
        }

        return reasons;
    }

    private static SecurityPrincipalContext BuildSecurityPrincipalContext(TableAccessQuery query, TimeProvider timeProvider)
    {
        var principal = query.Principal;
        var realmKind = SecurityPrincipalContext.ReadPipelineRealmKind(principal);
        var hasPipelineMarker = principal?.Claims.Any(static c =>
            string.Equals(c.Type, GroupResolutionClaims.MarkerClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal)) == true;
        var groupStatus = hasPipelineMarker
            ? GroupResolutionClaims.Read(principal, timeProvider.GetUtcNow())
            : GroupResolutionStatus.Complete;
        var groupsExpireAt = GroupResolutionClaims.ReadExpiresAt(principal);
        return new SecurityPrincipalContext
        {
            UserSid = query.UserSid,
            ResolvedTenant = query.Tenant,
            GroupSids = query.GroupSids,
            GroupResolutionStatus = groupStatus,
            GroupsExpireAt = groupsExpireAt,
            TenantRoles = new HashSet<string>(query.Roles, StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = principal?.Identity?.AuthenticationType ?? "Token",
            IsAuthenticated = principal?.Identity?.IsAuthenticated ?? true,
            RealmKind = realmKind,
            ClientIp = query.ClientIp,
            Claims = query.Claims ?? principal?.Claims,
            Principal = principal
        };
    }

    private static TableAccessDecision ApplyProtectionMasks(
        TableAccessDecision decision,
        TableMetadata metadata,
        EffectiveProtection protection)
    {
        if (!decision.IsAllowed)
        {
            return decision;
        }

        Dictionary<string, ColumnAccessLevel>? updatedAccess = null;
        foreach (var col in metadata.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.ColumnName))
            {
                continue;
            }

            var effectiveCategory = protection.Columns.TryGetValue(col.ColumnName, out var colProt)
                ? colProt.EffectiveMaskCategory
                : protection.TableMaskCategory;
            if (string.IsNullOrWhiteSpace(effectiveCategory))
            {
                continue;
            }

            var currentLevel = decision.GetEffectiveColumnAccess(col.ColumnName, metadata);
            if (currentLevel is ColumnAccessLevel.Deny or ColumnAccessLevel.RowDependent or ColumnAccessLevel.Mask)
            {
                continue;
            }

            updatedAccess ??= new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
            updatedAccess[col.ColumnName] = ColumnAccessLevel.Mask;
        }

        return updatedAccess is null
            ? decision
            : decision with { ColumnAccess = updatedAccess };
    }
}
