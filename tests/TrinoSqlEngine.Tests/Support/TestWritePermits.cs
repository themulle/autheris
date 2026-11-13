using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// Test-only default write permit (WP-SIMPL-45 PR 3 CI repair, R-1). It lets DML tests reach the checks they assert, because the engine
/// rejects every DML statement without a <see cref="DmlWritePermit"/> (SEC-SIMPL-120). The permit grants every catalog column of the
/// target except masked ones (a masked column is writable only when named in <c>alsoWritable</c>) and marks nothing write-protected.
/// It never replaces a non-null <see cref="GovernancePolicy.Write"/> (SEC-CIFIX-4).
/// </summary>
internal static class TestWritePermits
{
    /// <summary>The exact rejection of every MERGE under a write permit (SEC-SIMPL-124, D-45-4).</summary>
    internal const string MergeNotSupportedMessage = "MERGE is not supported for governed writes.";

    /// <summary>The exact rejection of every DML statement compiled without a write permit (SEC-SIMPL-120).</summary>
    internal const string PermitRequiredMessage = "A write permit is required for DML operations.";

    /// <summary>The rejection of a written column outside the permit's writable set (SEC-SIMPL-121).</summary>
    internal const string NotCoveredMessage = "A column is not covered by a write grant.";

    private static readonly FastSqlEngine ParseEngine = new() { MaxQueryLength = int.MaxValue, ParseTimeout = TimeSpan.Zero };

    /// <summary>
    /// The permit for <paramref name="sql"/>, or null for SELECT and unparseable SQL (the engine then reports its own error). When the
    /// operation is known but the target does not resolve, the writable set is empty.
    /// </summary>
    internal static DmlWritePermit? For(string sql, GovernancePolicy policy, IEnumerable<string>? alsoWritable = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Write is not null) return policy.Write;

        SqlStatement statement;
        try
        {
            var (tree, _) = ParseEngine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
            statement = new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false, EnforceFunctionPolicy = false }).BuildStatement(tree);
        }
        catch (Exception)
        {
            return null;
        }

        (DmlWriteOperation Operation, NamedTableSource Target)? dml = statement switch
        {
            InsertStatement i => (DmlWriteOperation.Insert, i.TargetTable),
            UpdateStatement u => (DmlWriteOperation.Update, u.TargetTable),
            DeleteStatement d => (DmlWriteOperation.Delete, d.TargetTable),
            MergeStatement m => (DmlWriteOperation.Update, m.Target),
            _ => null
        };
        if (dml is null) return null;

        var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TableCatalogEntry? entry = null;
        try
        {
            entry = policy.Catalog.Resolve(dml.Value.Target.Name);
        }
        catch (Exception)
        {
            // An unresolvable target keeps the empty writable set (fail closed).
        }

        if (entry is not null)
        {
            foreach (var column in entry.Columns)
            {
                if (!policy.Masks.HasMask(entry.Identity, column.Name)) writable.Add(column.Name);
            }

            if (alsoWritable is not null)
            {
                foreach (var name in alsoWritable) writable.Add(name);
            }
        }

        return new DmlWritePermit(dml.Value.Operation, writable, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Applies <see cref="For"/> to the request's policy unless the policy already carries a permit.</summary>
    internal static CompileRequest WithTestWritePermit(this CompileRequest request, string sql, IEnumerable<string>? alsoWritable = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Policy.Write is not null) return request;
        var permit = For(sql, request.Policy, alsoWritable);
        return permit is null ? request : request with { Policy = request.Policy with { Write = permit } };
    }
}
