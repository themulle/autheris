namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// Issue #132 (PLAN-DATA-132, SEC-DATA132-02, SEC-ADG-02): Post-compile AST visitor asserting that
/// every physical table reference in the generated SQL strictly maps to an authorized (database, schema, table) target.
/// Any extraneous or injected table reference throws SecurityException (SEC-ADG-02).
/// </summary>
public sealed class PostCompileTableTargetAssertionVisitor : SqlAstRewriter
{
    private readonly HashSet<string> _authorizedTargets;

    public PostCompileTableTargetAssertionVisitor(IEnumerable<(string? Database, string? Schema, string Table)> authorizedTargets)
    {
        ArgumentNullException.ThrowIfNull(authorizedTargets);
        _authorizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (db, schema, table) in authorizedTargets)
        {
            if (!string.IsNullOrWhiteSpace(db) && !string.IsNullOrWhiteSpace(schema))
            {
                _authorizedTargets.Add($"{db}.{schema}.{table}");
            }

            if (!string.IsNullOrWhiteSpace(schema))
            {
                _authorizedTargets.Add($"{schema}.{table}");
            }

            _authorizedTargets.Add(table);
        }
    }

    public PostCompileTableTargetAssertionVisitor(IEnumerable<string> authorizedTargetNames)
    {
        ArgumentNullException.ThrowIfNull(authorizedTargetNames);
        _authorizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in authorizedTargetNames)
        {
            _authorizedTargets.Add(name);
            var parts = name.Split('.');
            if (parts.Length == 3)
            {
                _authorizedTargets.Add($"{parts[1]}.{parts[2]}");
                _authorizedTargets.Add(parts[2]);
            }
            else if (parts.Length == 2)
            {
                _authorizedTargets.Add(parts[1]);
            }
        }
    }

    public static void AssertValid(SqlNode node, IEnumerable<(string? Database, string? Schema, string Table)> authorizedTargets)
    {
        ArgumentNullException.ThrowIfNull(node);
        var visitor = new PostCompileTableTargetAssertionVisitor(authorizedTargets);
        visitor.Visit(node);
    }

    public static void AssertValid(SqlNode node, IEnumerable<string> authorizedTargetNames)
    {
        ArgumentNullException.ThrowIfNull(node);
        var visitor = new PostCompileTableTargetAssertionVisitor(authorizedTargetNames);
        visitor.Visit(node);
    }

    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        string? db = null;
        string? schema = null;
        string table;

        if (node.Name.Parts.Count >= 3)
        {
            db = node.Name.Parts[0].Value;
            schema = node.Name.Parts[1].Value;
            table = node.Name.Parts[2].Value;
        }
        else if (node.Name.Parts.Count == 2)
        {
            schema = node.Name.Parts[0].Value;
            table = node.Name.Parts[1].Value;
        }
        else if (node.Name.Parts.Count == 1)
        {
            table = node.Name.Parts[0].Value;
        }
        else
        {
            table = node.Name.NormalizedName;
        }

        string fullKey = !string.IsNullOrWhiteSpace(db) && !string.IsNullOrWhiteSpace(schema)
            ? $"{db}.{schema}.{table}"
            : !string.IsNullOrWhiteSpace(schema)
                ? $"{schema}.{table}"
                : table;

        string schemaKey = !string.IsNullOrWhiteSpace(schema) ? $"{schema}.{table}" : table;

        if (!_authorizedTargets.Contains(fullKey) &&
            !_authorizedTargets.Contains(schemaKey) &&
            !_authorizedTargets.Contains(table))
        {
            throw new SecurityException(
                $"Post-compile table assertion failed: table reference '{node.Name}' is not in the authorized targets. (SEC-ADG-02)");
        }

        return base.VisitNamedTableSource(node);
    }
}
