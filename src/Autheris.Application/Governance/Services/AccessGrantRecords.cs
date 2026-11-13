namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>The grant records an applied access plan creates: one Allow record per planned grant, scoped to the caller tenant.</summary>
internal static class AccessGrantRecords
{
    /// <summary>
    /// A plan principal is a user, a group or a service principal identifier (<c>user:</c>, <c>group:</c>, <c>sp:</c> or a bare id).
    /// Roles, usersets and every other entity form are not a grantee of a grant record.
    /// </summary>
    internal static bool TryParseGrantee(string? principal, out GranteeType type, out string id)
    {
        type = GranteeType.User;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(principal))
        {
            return false;
        }

        var trimmed = principal.Trim();
        var rest = trimmed;
        if (trimmed.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            rest = trimmed[5..];
        }
        else if (trimmed.StartsWith("sp:", StringComparison.OrdinalIgnoreCase))
        {
            type = GranteeType.ServicePrincipal;
            rest = trimmed[3..];
        }
        else if (trimmed.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
        {
            type = GranteeType.Group;
            rest = trimmed[6..];
        }

        rest = rest.Trim();
        if (rest.Length is 0 or > 256 || rest.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '@' or '|' or '/' or '\\' or '$')))
        {
            return false;
        }

        id = rest;
        return true;
    }

    internal static List<Consent> Build(
        IReadOnlyList<PrincipalAccessGrant> grants,
        TableMetadata table,
        TableIdentifier tableId,
        TenantId tenant,
        string adminSid,
        DateTimeOffset now,
        IReadOnlyList<DateTimeOffset?> maxValidTo)
    {
        var consents = new List<Consent>(grants.Count);
        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i];
            if (!TryParseGrantee(grant.Principal, out var granteeType, out var granteeId))
            {
                throw new SecurityException($"Invalid principal '{grant.Principal}' in access plan.");
            }

            var consentId = Guid.NewGuid();
            consents.Add(new Consent
            {
                Id = consentId,
                TableId = table.Table.Id,
                TableIdentifier = tableId,
                TenantId = tenant,
                Effect = ConsentEffect.Allow,
                GranteeType = granteeType,
                GranteeSid = new Sid(granteeId),
                CreatedBySid = new Sid(adminSid),
                ValidFrom = now,
                ValidTo = grant.ValidUntil ?? maxValidTo[i] ?? now.AddYears(1),
                ColumnRules = grant.Columns.Select(c => new ConsentColumnRule
                {
                    ConsentId = consentId,
                    TableColumnId = table.Columns.FirstOrDefault(col => string.Equals(col.ColumnName, c.Key, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.NewGuid(),
                    ColumnName = c.Key,
                    AccessLevel = ParseAccessLevel(c.Value)
                }).ToList(),
                RowFilterCondition = string.IsNullOrWhiteSpace(grant.RowFilter) ? null : grant.RowFilter
            });
        }

        return consents;
    }

    private static ColumnAccessLevel ParseAccessLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "clear" => ColumnAccessLevel.Clear,
        "mask" or "masked" => ColumnAccessLevel.Mask,
        _ => ColumnAccessLevel.Deny
    };
}
