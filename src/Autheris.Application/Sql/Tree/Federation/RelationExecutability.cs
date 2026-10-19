using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree.Federation;

/// <summary>
/// Single source of truth for "can the inner tree service run a relation between these two tables". Used by the GraphQL
/// catalog schema (to omit relations that cannot run) and by <see cref="GovernedTreeQueryService"/> (to reject them),
/// so the schema and the runtime cannot drift apart. Depends on catalog metadata only, never on the caller.
/// </summary>
public static class RelationExecutability
{
    /// <summary>
    /// True when both tables name the same data source connection. The name is compared with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> because the connection map is case-insensitive; no trimming and no
    /// other normalization. Empty equals empty (the runtime then fails with "has no database connection"); an empty
    /// name never matches a non-empty one. <see cref="TableIdentifier.Domain"/> is deliberately not compared: relations
    /// across domains of one connection run as one governed statement.
    /// </summary>
    public static bool HasSameSourceName(TableMetadata first, TableMetadata second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return string.Equals(first.Table.SourceName, second.Table.SourceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the tree service can run a relation between the two tables as one statement: same
    /// <see cref="DataSourceType"/>, same data source name (<see cref="HasSameSourceName"/>) and the same SQL dialect.
    /// Unparseable dialects are never executable (fail closed). The runtime additionally compares the dialect with the
    /// provider of the connection, which depends on configuration and stays in the tree service.
    /// </summary>
    public static bool IsSameExecutionSource(TableMetadata parent, TableMetadata child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        if (parent.DataSourceType != child.DataSourceType || !HasSameSourceName(parent, child))
        {
            return false;
        }

        return DatabaseDialectExtensions.TryParseDialect(parent.Table.SourceType, out var parentDialect)
            && DatabaseDialectExtensions.TryParseDialect(child.Table.SourceType, out var childDialect)
            && parentDialect == childDialect;
    }

    /// <summary>
    /// Issue #75: True when a relation between <paramref name="parent"/> and <paramref name="child"/> can be executed:
    /// either within the same SQL source (<see cref="IsSameExecutionSource"/>) or across distinct data sources
    /// when <paramref name="crossSourceEnabled"/> is true and <paramref name="relation"/> permits cross-data-source resolution.
    /// </summary>
    public static bool IsExecutable(TableRelation relation, TableMetadata parent, TableMetadata child, bool crossSourceEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        if (IsSameExecutionSource(parent, child))
        {
            return true;
        }

        return crossSourceEnabled && relation.AllowCrossDataSource;
    }
}
