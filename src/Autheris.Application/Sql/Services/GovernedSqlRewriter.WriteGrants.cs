namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Internal;

public sealed partial class GovernedSqlRewriter
{
    private static bool ValidateDmlStatementAndTransport(SqlQueryMetadata metadata, DmlAuditContext? dmlContext, string? transport)
    {
        if (metadata.IsMerge)
        {
            if (dmlContext != null)
            {
                dmlContext.StatementType = SqlStatementType.Update;
                dmlContext.Tables = metadata.ReferencedTables
                    .Select(t => t.FullName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            throw new WebSqlPolicyException(
                "MERGE is not supported for governed writes.",
                GovernedSqlErrorCodes.StatementNotPermitted);
        }

        bool isDml = metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
        if (isDml && dmlContext != null)
        {
            dmlContext.StatementType = metadata.StatementType;
            dmlContext.Tables = metadata.ReferencedTables
                .Select(t => t.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (isDml && !DmlTransports.IsAllowed(transport))
        {
            throw new WebSqlPolicyException(
                "DML statements are not wired for this transport (dml_transport_not_wired).",
                "dml_transport_not_wired");
        }

        return isDml;
    }

    private async Task<(TableAccessDecision Decision, DmlWritePermit Permit, long? MaxRowsCap)> AuthorizeDmlTargetAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        TableMetadata tableMeta,
        TableAccessTarget target,
        SqlQueryMetadata metadata,
        IPAddress clientIp,
        string? transport,
        CancellationToken ct)
    {
        var decision = await DecideWriteTargetAsync(user, tenantId, tableMeta, metadata, clientIp, transport, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.WriteGrant is null)
        {
            _logger?.LogWarning("WebSQL DML write access to table {Table} denied: {Reasons}", target.FullName, string.Join("; ", decision.DeniedReasons));
            throw SqlDataSourceResolver.TableDenied(target);
        }

        var permit = BuildWritePermit(metadata.StatementType, tableMeta, decision.WriteGrant);
        return (decision, permit, decision.WriteGrant.MaxRowsPerStatement);
    }

    private async Task<TableAccessDecision> DecideWriteTargetAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        TableMetadata tableMeta,
        SqlQueryMetadata metadata,
        IPAddress clientIp,
        string? transport,
        CancellationToken ct)
    {
        var operation = metadata.StatementType switch
        {
            SqlStatementType.Insert => AccessOperation.Insert,
            SqlStatementType.Update => AccessOperation.Update,
            SqlStatementType.Delete => AccessOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };

        var accessTransport = MapWriteTransport(transport);
        var writtenCols = operation is AccessOperation.Insert or AccessOperation.Update && metadata.WrittenColumns is { Count: > 0 }
            ? metadata.WrittenColumns.ToArray()
            : null;

        var secContext = SecurityPrincipalContext.FromPrincipal(user, tenantId, clientIp: clientIp);
        var writeRequest = new AccessRequest(
            secContext,
            tableMeta,
            operation,
            accessTransport,
            Columns: writtenCols);

        return await _accessDecisionPoint.DecideAsync(writeRequest, ct).ConfigureAwait(false);
    }

    private static AccessTransport MapWriteTransport(string? transport)
    {
        if (string.Equals(transport, SqlTransports.WebSql, StringComparison.OrdinalIgnoreCase)) return AccessTransport.WebSql;
        if (string.Equals(transport, SqlTransports.Trino, StringComparison.OrdinalIgnoreCase)) return AccessTransport.Trino;
        if (string.Equals(transport, SqlTransports.Rest, StringComparison.OrdinalIgnoreCase)) return AccessTransport.Rest;
        if (string.Equals(transport, SqlTransports.Mcp, StringComparison.OrdinalIgnoreCase)) return AccessTransport.Mcp;
        if (string.Equals(transport, SqlTransports.ArrowExport, StringComparison.OrdinalIgnoreCase)) return AccessTransport.ArrowExport;
        if (string.Equals(transport, SqlTransports.Olap, StringComparison.OrdinalIgnoreCase)) return AccessTransport.Olap;
        return AccessTransport.Internal;
    }

    private static DmlWritePermit BuildWritePermit(
        SqlStatementType statementType,
        TableMetadata tableMeta,
        TableWriteDecision writeGrant)
    {
        var permitOp = statementType switch
        {
            SqlStatementType.Insert => DmlWriteOperation.Insert,
            SqlStatementType.Update => DmlWriteOperation.Update,
            SqlStatementType.Delete => DmlWriteOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(statementType))
        };

        var protectedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in tableMeta.Columns)
        {
            if (col.WriteProtected)
            {
                protectedCols.Add(col.ColumnName);
            }
        }

        if (statementType == SqlStatementType.Update && tableMeta.PrimaryKeyColumns.Count > 0)
        {
            foreach (var pk in tableMeta.PrimaryKeyColumns)
            {
                protectedCols.Add(pk);
            }
        }

        var policy = writeGrant.Policy;
        if (statementType == SqlStatementType.Update && policy?.ServerStampedColumns is { Count: > 0 } stamped)
        {
            foreach (var colName in stamped.Keys)
            {
                protectedCols.Add(colName);
            }
        }

        bool bounded = policy is { AllowsWrites: true, EffectiveRequirePrimaryKeyPredicate: true };
        IReadOnlyList<string>? pkCols = bounded && tableMeta.PrimaryKeyColumns.Count > 0
            ? tableMeta.PrimaryKeyColumns
            : null;

        return new DmlWritePermit(
            permitOp,
            writeGrant.WritableColumns.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            protectedCols.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            BoundedSingleTable: bounded,
            PrimaryKeyColumns: pkCols,
            BoundedMaxRows: bounded ? writeGrant.MaxRowsPerStatement ?? policy!.MaxRowsPerStatement ?? TableWritePolicy.DefaultBoundedMaxRows : null);
    }

    // A DML target without a tenant column is exempt only by its exact schema-qualified name (case-insensitive, never by suffix or bare table name).
    private static bool IsDmlTenantExempt(TableIdentifier id, IEnumerable<string>? exemptTables)
    {
        string qualified = id.ToQualifiedName();
        return exemptTables != null && exemptTables.Any(e => string.Equals(e?.Trim(), qualified, StringComparison.OrdinalIgnoreCase));
    }
}
