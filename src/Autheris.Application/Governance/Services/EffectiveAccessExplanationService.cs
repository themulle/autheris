namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed class EffectiveAccessExplanationService(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IConsentResolutionService resolutionService) : IEffectiveAccessExplanationService
{
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
        if (metadata == null)
        {
            return new EffectiveTableAccessDto(
                table.ToString(),
                "DENIED",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Table not found in catalog.",
                null
            );
        }

        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var allUserSids = principal.GetAllUserSids();
        var callerScopes = TokenAccessScope.GetScopeSet(principal);
        var tenantId = principal.GetTenantId();

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

        var allColumnNames = metadata.Columns.Select(c => c.ColumnName).ToList();

        if (!decision.IsAllowed)
        {
            return new EffectiveTableAccessDto(
                table.ToString(),
                "DENIED",
                Array.Empty<string>(),
                Array.Empty<string>(),
                allColumnNames,
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
            activeRowFilters.Add(decision.CombinedRowFilterSql);
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
}
