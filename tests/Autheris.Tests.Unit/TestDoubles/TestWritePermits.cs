namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Test-only default write permit (WP-SIMPL-45 PR 3 CI repair, R-1). Production derives no permit from roles, so every DML
/// statement is denied until the decision point is wired. Tests that exercise the pipeline behind the permit check build the
/// permit the statement itself needs: the target's unmasked catalog columns, no write-protected column. The helper never
/// widens a permit that is already set (SEC-CIFIX-4) and is never referenced from <c>src/</c>.
/// </summary>
internal static class TestWritePermits
{
    private static readonly FastSqlEngine Parser = new();

    /// <summary>
    /// The permit a test statement needs, or null for non-DML and for unparseable SQL (the engine then reports its own error).
    /// An unresolved target yields an empty writable set so the engine keeps its unknown-target message.
    /// </summary>
    internal static DmlWritePermit? For(string sql, GovernancePolicy policy, IEnumerable<string>? alsoWritable = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(policy);

        SqlStatement statement;
        try
        {
            var (tree, _) = Parser.Parse(sql.AsMemory(), SqlTokenSecurityOptions.Strict);
            statement = new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false }).BuildStatement(tree);
        }
        catch (Exception)
        {
            return null;
        }

        var (operation, target) = statement switch
        {
            InsertStatement s => (DmlWriteOperation.Insert, s.TargetTable),
            UpdateStatement s => (DmlWriteOperation.Update, s.TargetTable),
            DeleteStatement s => (DmlWriteOperation.Delete, s.TargetTable),
            // MERGE is rejected by the engine (D-45-4); the permit only lets the test reach that rejection.
            MergeStatement s => (DmlWriteOperation.Update, s.Target),
            _ => (default(DmlWriteOperation), null)
        };
        if (target is null)
        {
            return null;
        }

        var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entry = policy.Catalog.Resolve(target.Name);
        if (entry is not null)
        {
            foreach (var column in entry.Columns)
            {
                if (!policy.Masks.HasMask(entry.Identity, column.Name))
                {
                    writable.Add(column.Name);
                }
            }

            if (alsoWritable is not null)
            {
                foreach (var column in alsoWritable)
                {
                    writable.Add(column);
                }
            }
        }

        return new DmlWritePermit(operation, writable, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Applies <see cref="For"/> to the request unless it already carries a permit (never replaces a non-null one).</summary>
    internal static CompileRequest WithTestWritePermit(this CompileRequest request, string sql, IEnumerable<string>? alsoWritable = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Policy.Write is not null)
        {
            return request;
        }

        var permit = For(sql, request.Policy, alsoWritable);
        return permit is null ? request : request with { Policy = request.Policy with { Write = permit } };
    }
}
