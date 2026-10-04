using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

/// <summary>
/// Centralized Zero-Trust & Side-Channel Inference Evaluator for Connector-SPI implementations.
/// Consolidates security enforcement across all connectors and prevents duplicated audit/security logic.
/// </summary>
public static class ConnectorSecurityPolicyEvaluator
{
    private static readonly HashSet<string> ReservedPaginationArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "limit", "offset", "first", "last", "after", "before", "take", "skip"
    };

    public static void EnforceSecurityPolicy(ConnectorSessionContext session, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);

        // SEC-AC-01: Zero-Trust / Fail-Closed: Refuse execution if table access is denied by policy
        if (!session.AccessDecision.IsAllowed)
        {
            var reasons = session.AccessDecision.DeniedReasons.Count > 0
                ? string.Join("; ", session.AccessDecision.DeniedReasons)
                : "Tabelle ist durch Zugriffsrichtlinie gesperrt.";
            throw new SecurityException($"Zero-Trust-Verletzung: Zugriff auf Tabelle '{metadata.Identifier}' verweigert: {reasons}");
        }

        // SEC-AC-02: Zero-Trust: Validate RLS filter early before any connection or query execution
        if (!string.IsNullOrWhiteSpace(session.PushdownFilterSql))
        {
            SqlSecurityValidator.ValidatePredicateSql(
                session.PushdownFilterSql,
                "PushdownFilterSql");
        }

        // SEC-01: Side-channel inference protection: verify column filters target only Clear columns
        if (session.Arguments != null && session.Arguments.Count > 0)
        {
            foreach (var (argKey, argVal) in session.Arguments)
            {
                if (ReservedPaginationArgs.Contains(argKey) || argVal == null)
                {
                    continue;
                }

                var matchingCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
                if (matchingCol != null)
                {
                    // SEC H-10: Filter only on effectively Clear columns (catalog-sensitive/masked columns need an explicit Clear).
                    var access = session.AccessDecision.GetEffectiveColumnAccess(matchingCol.ColumnName, metadata);
                    if (access != ColumnAccessLevel.Clear)
                    {
                        throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{matchingCol.ColumnName}' in Tabelle '{metadata.Identifier}' ist nicht gestattet (Zugriffsebene: {access}).");
                    }
                }
            }
        }
    }
}
