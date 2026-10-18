namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Autheris.Application.Common;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

public sealed record CoLocatedSubtreePlan(
    string GroupName,
    IReadOnlyList<string> Databases,
    IReadOnlyList<ResolvedSourceTable> Tables,
    string CompiledSql,
    bool EmitThreePartNames);

/// <summary>
/// Issue #132 (PLAN-DATA-132, WP-132-3, SEC-DATA132-02, SEC-DATA132-04):
/// Native co-located SQL Server subtree compiler.
/// Enforces cross-database RLS isolation, asserts post-compile table references,
/// and produces three-part or two-part SQL Server statements.
/// </summary>
public static class SubtreeCompiler
{
    /// <summary>
    /// SEC-DATA132-04: Cross-Database Policy Isolation.
    /// Asserts that table-level RLS policies (CombinedRowFilterSql) do not reference tables in other databases.
    /// Correlated cross-database RLS policies fail closed.
    /// </summary>
    public static void ValidatePolicyIsolation(
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var table in tables)
        {
            var id = table.Metadata.Identifier;
            if (!decisions.TryGetValue(id, out var dec) || dec == null) continue;

            if (!string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql))
            {
                var rowFilter = dec.CombinedRowFilterSql;
                var currentDb = table.Metadata.Table.PhysicalDatabaseName ?? table.Metadata.Identifier.Domain;

                foreach (var other in tables)
                {
                    var otherDb = other.Metadata.Table.PhysicalDatabaseName ?? other.Metadata.Identifier.Domain;
                    if (string.Equals(currentDb, otherDb, StringComparison.OrdinalIgnoreCase)) continue;

                    // Correlated subquery or table reference spanning to another database
                    if (rowFilter.Contains(otherDb, StringComparison.OrdinalIgnoreCase) ||
                        rowFilter.Contains(other.Metadata.Identifier.TableName, StringComparison.OrdinalIgnoreCase) ||
                        rowFilter.Contains(other.Target.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new GatewaySecurityException(
                            $"Cross-database correlated row filter subqueries are not permitted; policy on '{id}' references external table or database '{otherDb}'.",
                            GovernedSqlErrorCodes.PolicyDenied);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Compiles a co-located multi-table statement for execution on a co-located SQL Server instance.
    /// </summary>
    public static CoLocatedSubtreePlan Compile(
        string coLocationGroup,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        SqlStatement statement,
        ISqlEngine? sqlEngine = null)
    {
        ArgumentNullException.ThrowIfNull(coLocationGroup);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(statement);

        // 1. Verify cross-database policy isolation (SEC-DATA132-04)
        ValidatePolicyIsolation(tables, decisions);

        // 2. Identify distinct physical databases
        var databases = tables
            .Select(t => t.Metadata.Table.PhysicalDatabaseName ?? t.Metadata.Identifier.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool multiDatabase = databases.Count > 1;

        // 3. Post-compile table reference assertion (SEC-DATA132-02, SEC-ADG-02)
        var authorizedTargets = tables.Select(t => (
            Database: (string?)(t.Metadata.Table.PhysicalDatabaseName ?? t.Metadata.Identifier.Domain),
            Schema: (string?)t.Metadata.Identifier.Schema,
            Table: t.Metadata.Identifier.TableName)).ToList();

        PostCompileTableTargetAssertionVisitor.AssertValid(statement, authorizedTargets);

        // 4. Emit SQL via SQL Server dialect generator
        var context = new SqlEmitterContext(TargetSqlDialect.SqlServer)
        {
            EmitThreePartNames = multiDatabase
        };

        var generator = new SqlServerDialectGenerator();
        var sql = generator.GenerateSql(statement, context);

        return new CoLocatedSubtreePlan(
            GroupName: coLocationGroup,
            Databases: databases,
            Tables: tables,
            CompiledSql: sql,
            EmitThreePartNames: multiDatabase);
    }
}
