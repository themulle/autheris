namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Security;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// The per-caller column levels of catalog-side outputs, shared by MCP <c>describe_dataset</c> and the REST catalog detail so that
/// both resolve a caller exactly the same way. An administrator view is Clear for every column; any other caller is resolved by the
/// data-path resolver (granted: consent decision, otherwise: catalog baseline lowered by Deny-effect consents), then lowered by the
/// catalog DENY masking rules. Every failure except cancellation yields Deny for every column (fail closed).
/// </summary>
internal static class CatalogCallerColumnLevels
{
    public static IReadOnlyDictionary<string, ColumnAccessLevel> Resolve(
        IConsentResolutionService consentResolution,
        ClaimsPrincipal principal,
        TableMetadata table,
        IReadOnlyList<Consent> activeConsents,
        bool isAdmin,
        bool isGranted,
        bool bypass,
        ILogger? logger)
    {
        Dictionary<string, ColumnAccessLevel> levels;
        if (isAdmin)
        {
            levels = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in table.Columns)
            {
                levels[col.ColumnName] = ColumnAccessLevel.Clear;
            }

            return levels;
        }

        try
        {
            // The explicit (production-blocked) MCP auth bypass may have no SID: it keeps the catalog baseline (AC-127-9).
            var userSid = principal.GetUserSid() ?? (bypass ? new Sid("MCP_AUTH_BYPASS") : (Sid?)null);
            if (userSid == null)
            {
                levels = CatalogColumnAccessResolver.AllDeny(table);
            }
            else
            {
                // DT-A-3: claims only; the explicit MCP auth bypass may carry no tenant (no tenant consent applies), every other caller fails closed.
                List<Consent> tenantConsents = bypass && Autheris.Domain.Tenancy.TenantClaimReader.Read(principal).Kind != Autheris.Domain.Tenancy.ClaimTenantKind.Valid
                    ? []
                    : activeConsents.Where(c => c.TenantId == principal.GetTenantId()).ToList();
                var groups = principal.GetGroupSids();
                var roles = principal.GetUserRoles();
                var scopes = Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims);

                levels = isGranted
                    ? CatalogColumnAccessResolver.ResolveGranted(consentResolution, table, userSid.Value, groups, roles, tenantConsents, scopes)
                    : CatalogColumnAccessResolver.ResolveBaseline(consentResolution, table, userSid.Value, groups, roles, tenantConsents, scopes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed; only the table and the exception type are logged (no consent content, scopes or values).
            logger?.LogWarning("Column level resolution failed for {Dataset} ({ExceptionType}); all columns denied.", table.Identifier.ToString(), ex.GetType().Name);
            levels = CatalogColumnAccessResolver.AllDeny(table);
        }

        CatalogColumnAccessResolver.ApplyCatalogDenyRules(levels, table);
        return levels;
    }
}
