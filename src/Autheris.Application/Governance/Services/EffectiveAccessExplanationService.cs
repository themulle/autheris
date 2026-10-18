namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class EffectiveAccessExplanationService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IConsentResolutionService resolutionService,
    IDataOwnershipRepository? ownershipRepository = null,
    IOptions<GatewayOptions>? gatewayOptions = null,
    IOptions<GovernanceTextOptions>? textOptions = null) : IEffectiveAccessExplanationService
{
    private int DescriptionMaxLength => (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;

    /// <summary>Upper bound of <see cref="EffectiveTableAccessDto.ConsentDescriptions"/> (decision 9).</summary>
    internal const int MaxDescriptions = 10;

    private TenancyMode EffectiveTenancyMode => gatewayOptions?.Value.Tenancy.EffectiveMode ?? TenancyMode.Multi;

    /// <summary>Answer for tables that do not exist or that the caller may not discover (identical on purpose).</summary>
    internal const string TableNotFoundMessage = "Table not found in catalog.";

    /// <summary>Row filter description for callers who are not allowed to see the filter definition.</summary>
    internal const string RowFilterDescription = "Row filter applies (defined by data owner).";

    /// <summary>REQ-GOV-129 (V-129-10): all a caller (and an owner) learns of a filter that depends on a caller attribute: that one applies. No attribute name, no value.</summary>
    internal const string AttributeRowFilterDescription = "Row filter applies (depends on an attribute of the caller).";

    public async Task<EffectiveTableAccessDto> ExplainMyAccessAsync(
        ClaimsPrincipal principal,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return new EffectiveTableAccessDto(
                table.ToString(),
                "DENIED",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Anonymous or unauthenticated principal has no access.",
                null
            );
        }

        var metadata = await metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        var tenantId = principal.GetTenantId();
        var roles = principal.GetUserRoles();
        var isAdmin = IsGovernanceAdmin(principal, roles);

        if (metadata == null)
        {
            // Same lookups as for an existing table, so unknown and hidden tables behave alike.
            await CatalogVisibility.VisibleTablesAsync(Array.Empty<TableMetadata>(), principal, tenantId, consentRepository, contract: null, EffectiveTenancyMode, gatewayOptions?.Value.Catalog.SharedTables, ct).ConfigureAwait(false);
            return NotFound(table);
        }

        var groupSids = principal.GetGroupSids();
        var allUserSids = principal.GetAllUserSids();
        // Claims overload, as the data path: it also reads a plain 'scope' claim (issue #127).
        var callerScopes = TokenAccessScope.GetScopeSet(principal.Claims);

        var subjects = (allUserSids != null && allUserSids.Count > 0 ? allUserSids : [userSid.Value])
            .Concat(groupSids)
            .Distinct()
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var activeConsents = (await consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, now, tenantId, ct).ConfigureAwait(false))
            .Where(c => c.TenantId == tenantId)
            .ToList();

        var decision = resolutionService.ResolveAccess(
            userSid.Value,
            groupSids,
            roles,
            table,
            activeConsents,
            metadata.Dialect,
            allUserSids,
            callerScopes
        );

        if (!decision.IsAllowed)
        {
            // Only tables the caller can discover in the catalog are described; everything else is answered
            // exactly like an unknown table (no columns, same message).
            var visible = await CatalogVisibility.VisibleTablesAsync([metadata], principal, tenantId, consentRepository, contract: null, EffectiveTenancyMode, gatewayOptions?.Value.Catalog.SharedTables, ct).ConfigureAwait(false);
            if (visible.Count == 0)
            {
                return NotFound(table);
            }

            return new EffectiveTableAccessDto(
                table.ToString(),
                "DENIED",
                Array.Empty<string>(),
                Array.Empty<string>(),
                visible[0].Columns.Select(c => c.ColumnName).ToList(),
                Array.Empty<string>(),
                decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "No active allow consent.",
                null
            );
        }

        var allowedCols = new List<string>();
        var maskedCols = new List<string>();
        var deniedCols = new List<string>();

        foreach (var col in metadata.Columns)
        {
            var level = decision.GetEffectiveColumnAccess(col.ColumnName, metadata);
            switch (level)
            {
                case ColumnAccessLevel.Clear:
                    allowedCols.Add(col.ColumnName);
                    break;
                case ColumnAccessLevel.Mask:
                    maskedCols.Add(col.ColumnName);
                    break;
                case ColumnAccessLevel.Deny:
                default:
                    deniedCols.Add(col.ColumnName);
                    break;
            }
        }

        var activeRowFilters = new List<string>();
        if (decision.ConsentFilterRecipe is { } recipe && Autheris.Application.Identity.Attributes.CallerAttributeRecipes.Names(recipe).Count > 0)
        {
            activeRowFilters.Add(AttributeRowFilterDescription);
        }
        else if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            // The SQL (literal values, internal rules) is shown only to owners, delegates and governance admins.
            var canSeeDefinition = isAdmin || await IsOwnerOrDelegateAsync(userSid.Value, table, ct).ConfigureAwait(false);
            activeRowFilters.Add(canSeeDefinition ? decision.CombinedRowFilterSql : RowFilterDescription);
        }

        var applicableConsents = MatchingAllowConsents(activeConsents, table, now, userSid.Value, groupSids, roles, allUserSids).ToList();
        await ConsentDescriptionLoader.LoadAsync(consentRepository, applicableConsents, ct).ConfigureAwait(false);

        // Earliest moment at which the caller's current effective access changes (clamped expiry of matching grants only).
        DateTimeOffset? validTo = null;
        foreach (var c in applicableConsents)
        {
            if (validTo is null || c.EffectiveValidTo < validTo)
            {
                validTo = c.EffectiveValidTo;
            }
        }

        // PLAN-GOV-158-INC1 decision 9 (SEC-GOV158I1-09): descriptions come only from the contributing consents (the set above), the most specific
        // grantee first (user or service principal, then group, then role), then the earliest expiry, then the id. Output only through ForOutput.
        var describing = applicableConsents
            .OrderBy(c => GranteeSpecificity(c.GranteeType))
            .ThenBy(c => c.EffectiveValidTo)
            .ThenBy(c => c.Id)
            .Select(c => ConsentDescriptionText.ForOutput(c.Description, DescriptionMaxLength))
            .ToList();
        var descriptions = describing.OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxDescriptions).ToList();
        var description = descriptions.FirstOrDefault();

        return new EffectiveTableAccessDto(
            table.ToString(),
            "ALLOWED",
            allowedCols,
            maskedCols,
            deniedCols,
            activeRowFilters,
            description,
            validTo
        )
        {
            ConsentDescriptions = descriptions
        };
    }

    private static int GranteeSpecificity(GranteeType type) => type switch
    {
        GranteeType.User or GranteeType.ServicePrincipal => 0,
        GranteeType.Group => 1,
        GranteeType.Role => 2,
        _ => 3
    };

    /// <summary>
    /// Allow consents that apply to the caller, by the very predicate of the access decision
    /// (<see cref="ConsentResolutionService.IsApplicable"/>). Everything the explanation shows about grants derives from this set.
    /// </summary>
    internal static List<Consent> MatchingAllowConsents(
        IReadOnlyList<Consent> consents,
        TableIdentifier table,
        DateTimeOffset now,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> roles,
        IReadOnlySet<Sid>? allUserSids) =>
        consents.Where(c => c.Effect == ConsentEffect.Allow && ConsentResolutionService.IsApplicable(c, table, now, userSid, groupSids, roles, allUserSids)).ToList();

    private static EffectiveTableAccessDto NotFound(TableIdentifier table) => new(
        table.ToString(),
        "DENIED",
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        TableNotFoundMessage,
        null);

    private static bool IsGovernanceAdmin(ClaimsPrincipal principal, IReadOnlySet<string> roles) =>
        roles.Contains("GovernanceAdmin") || ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

    /// <summary>Data owner or active delegate of the table (fail-closed: errors mean no).</summary>
    private async Task<bool> IsOwnerOrDelegateAsync(Sid userSid, TableIdentifier table, CancellationToken ct)
    {
        if (ownershipRepository == null)
        {
            return false;
        }

        try
        {
            return await ownershipRepository.IsAuthorizedApproverForTableAsync(table, userSid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
