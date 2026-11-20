namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

public sealed partial class GovernedSqlRewriter
{
    private static (string Sql, MetricViewGuardResult Guard) EnforceMetricViewGuard(
        string rawSql,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        DatabaseDialect targetDatabaseDialect)
    {
        var guard = MetricViewStatementGuard.Enforce(rawSql, decidedTableMetadata, decisions, targetDatabaseDialect);
        return (guard.AugmentedSql ?? rawSql, guard);
    }

    private static IReadOnlySet<string> ApplyMetricViewAllowedFunctions(
        IReadOnlySet<string> allowedFunctions,
        TargetSqlDialect targetSqlDialect,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata)
    {
        if (targetSqlDialect != TargetSqlDialect.Databricks ||
            !decidedTableMetadata.Values.Any(static t => t.Table.ObjectKind == DiscoveryObjectKind.MetricView))
        {
            return allowedFunctions;
        }

        return new HashSet<string>(allowedFunctions, StringComparer.OrdinalIgnoreCase) { "measure" };
    }
}
