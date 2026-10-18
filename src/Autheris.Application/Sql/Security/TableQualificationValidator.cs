namespace Autheris.Application.Sql.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// Issue #132 (PLAN-DATA-132, SEC-DATA132-01): Client database qualification AST guard.
/// Asserts client SQL queries reference catalog/schema notation (catalog.schema.table or schema.table).
/// Rejects explicit three-part or four-part physical database names with INVALID_IDENTIFIER_QUALIFICATION.
/// </summary>
public sealed class TableQualificationValidator : SqlAstRewriter
{
    private readonly IReadOnlySet<string>? _authorizedCatalogs;

    public TableQualificationValidator(IReadOnlySet<string>? authorizedCatalogs = null)
    {
        _authorizedCatalogs = authorizedCatalogs;
    }

    public static void Validate(SqlStatement statement, IReadOnlySet<string>? authorizedCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var validator = new TableQualificationValidator(authorizedCatalogs);
        validator.Visit(statement);
    }

    public static void Validate(SqlQueryMetadata metadata, IReadOnlySet<string>? authorizedCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var target in metadata.ReferencedTables)
        {
            ValidateTarget(target, authorizedCatalogs);
        }
    }

    public static void Validate(string rawSql, IReadOnlySet<string>? authorizedCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(rawSql);
        var (tree, _) = FastSqlEngine.Default.Parse(rawSql.AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);
        Validate(statement, authorizedCatalogs);
    }

    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        // 4-part or higher names are strictly forbidden
        if (node.Name.Parts.Count >= 4)
        {
            throw new WebSqlPolicyException(
                $"Database-qualified or server-qualified object names are not permitted: '{node.Name}'. (INVALID_IDENTIFIER_QUALIFICATION)",
                GovernedSqlErrorCodes.InvalidIdentifierQualification);
        }

        // 3-part names must be valid registered catalog identifiers, not physical database names
        if (node.Name.Parts.Count == 3)
        {
            var firstPart = node.Name.Parts[0].Value;
            if (_authorizedCatalogs == null || !_authorizedCatalogs.Contains(firstPart))
            {
                throw new WebSqlPolicyException(
                    $"Explicit database qualification '{node.Name}' is not permitted in WebSQL. (INVALID_IDENTIFIER_QUALIFICATION)",
                    GovernedSqlErrorCodes.InvalidIdentifierQualification);
            }
        }

        return base.VisitNamedTableSource(node);
    }

    private static void ValidateTarget(TableAccessTarget target, IReadOnlySet<string>? authorizedCatalogs)
    {
        if (!string.IsNullOrWhiteSpace(target.FullName))
        {
            var parts = target.FullName.Split('.');
            if (parts.Length >= 4)
            {
                throw new WebSqlPolicyException(
                    $"Database-qualified or server-qualified object names are not permitted: '{target.FullName}'. (INVALID_IDENTIFIER_QUALIFICATION)",
                    GovernedSqlErrorCodes.InvalidIdentifierQualification);
            }
        }

        if (!string.IsNullOrWhiteSpace(target.Catalog))
        {
            if (authorizedCatalogs == null || !authorizedCatalogs.Contains(target.Catalog))
            {
                throw new WebSqlPolicyException(
                    $"Explicit database qualification '{target.FullName}' is not permitted in WebSQL. (INVALID_IDENTIFIER_QUALIFICATION)",
                    GovernedSqlErrorCodes.InvalidIdentifierQualification);
            }
        }
    }
}
