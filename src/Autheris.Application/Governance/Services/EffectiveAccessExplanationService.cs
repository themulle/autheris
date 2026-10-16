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

public sealed class EffectiveAccessExplanationService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IConsentResolutionService resolutionService,
    IDataOwnershipRepository? ownershipRepository = null) : IEffectiveAccessExplanationService
{
    /// <summary>Answer for tables that do not exist or that the caller may not discover (identical on purpose).</summary>
    internal const string TableNotFoundMessage = "Table not found in catalog.";

    /// <summary>Row filter description for callers who are not allowed to see the filter definition.</summary>
    internal const string RowFilterDescription = "Row filter applies (defined by data owner).";

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
            await CatalogVisibility.VisibleTablesAsync(Array.Empty<TableMetadata>(), principal, tenantId, consentRepository, ct).ConfigureAwait(false);
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
            var visible = await CatalogVisibility.VisibleTablesAsync([metadata], principal, tenantId, consentRepository, ct).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            // The SQL (literal values, internal rules) is shown only to owners, delegates and governance admins.
            var canSeeDefinition = isAdmin || await IsOwnerOrDelegateAsync(userSid.Value, table, ct).ConfigureAwait(false);
            activeRowFilters.Add(canSeeDefinition ? decision.CombinedRowFilterSql : RowFilterDescription);
        }

        var applicableConsents = activeConsents
            .Where(c => c.Effect == ConsentEffect.Allow && c.IsActive(now))
            .ToList();

        var validTo = applicableConsents.Count > 0
            ? applicableConsents.Min(c => c.ValidTo)
            : (DateTimeOffset?)null;

        var description = applicableConsents.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.RoleName))?.RoleName
            ?? $"Active grant for subject {userSid.Value}";

        return new EffectiveTableAccessDto(
            table.ToString(),
            "ALLOWED",
            allowedCols,
            maskedCols,
            deniedCols,
            activeRowFilters,
            description,
            validTo
        );
    }

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
