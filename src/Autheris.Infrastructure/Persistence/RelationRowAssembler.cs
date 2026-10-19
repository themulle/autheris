using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>One row of the relation read statement (<c>TABLE_RELATIONS</c> joined to the two tables and LEFT JOINed to <c>TABLE_RELATION_KEYS</c>).</summary>
internal readonly record struct RelationRowData(
    string Id,
    string ParentTableId,
    string ChildTableId,
    string RelationName,
    string Cardinality,
    TableIdentifier Parent,
    TableIdentifier Child,
    int? KeyOrdinal,
    string? ParentColumn,
    string? ChildColumn,
    bool AllowCrossDataSource = false);

/// <summary>
/// Engine-neutral assembly of <see cref="TableRelation"/> from the single relation read statement (issue #151, PLAN-REL-151 decision 5).
/// Rows must be ordered by relation id, then key ordinal. A relation without key rows, with an ordinal gap or violating
/// <see cref="RelationKeyRules"/> is skipped (fail closed); the load continues. Logging is bounded per load (SEC-REL151-09).
/// </summary>
internal static class RelationRowAssembler
{
    /// <summary>Maximum number of per-relation detail lines per load; one summary line is always logged first.</summary>
    internal const int MaxDetailLogLines = 10;

    public static IReadOnlyList<TableRelation> Assemble(IEnumerable<RelationRowData> rows, ILogger? logger = null)
    {
        var result = new List<TableRelation>();
        var skipped = new List<(string Id, string Reason)>();

        string? currentId = null;
        RelationRowData head = default;
        var parentKeys = new List<string>();
        var childKeys = new List<string>();
        var ordinals = new List<int>();
        var hasKeys = false;

        void Flush()
        {
            if (currentId is null)
            {
                return;
            }

            var reason = Validate(hasKeys, ordinals, parentKeys, childKeys);
            if (reason is not null)
            {
                skipped.Add((currentId, reason));
            }
            else if (Guid.TryParse(head.Id, out var id) && Guid.TryParse(head.ParentTableId, out var parentId) && Guid.TryParse(head.ChildTableId, out var childId))
            {
                result.Add(new TableRelation
                {
                    Id = id,
                    ParentTableId = parentId,
                    ParentTableIdentifier = head.Parent,
                    ChildTableId = childId,
                    ChildTableIdentifier = head.Child,
                    RelationName = head.RelationName,
                    JoinKeysParent = [.. parentKeys],
                    JoinKeysChild = [.. childKeys],
                    Cardinality = Enum.TryParse<RelationCardinality>(head.Cardinality, true, out var card) ? card : RelationCardinality.OneToMany,
                    AllowCrossDataSource = head.AllowCrossDataSource
                });
            }
            else
            {
                skipped.Add((currentId, "unreadable identifier"));
            }

            parentKeys.Clear();
            childKeys.Clear();
            ordinals.Clear();
            hasKeys = false;
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.Id, currentId, StringComparison.Ordinal))
            {
                Flush();
                currentId = row.Id;
                head = row;
            }

            if (row.KeyOrdinal is { } ordinal && row.ParentColumn is not null && row.ChildColumn is not null)
            {
                hasKeys = true;
                ordinals.Add(ordinal);
                parentKeys.Add(row.ParentColumn);
                childKeys.Add(row.ChildColumn);
            }
        }

        Flush();

        if (skipped.Count > 0 && logger is not null)
        {
            logger.LogError("{Skipped} relation(s) skipped because their key rows are missing or invalid.", skipped.Count);
            foreach (var (id, reason) in skipped.Take(MaxDetailLogLines))
            {
                logger.LogError("Relation {RelationId} skipped: {Reason}.", id, reason);
            }
        }

        return result;
    }

    private static string? Validate(bool hasKeys, List<int> ordinals, List<string> parent, List<string> child)
    {
        if (!hasKeys)
        {
            return "no key rows";
        }

        for (var i = 0; i < ordinals.Count; i++)
        {
            if (ordinals[i] != i + 1)
            {
                return "ordinal gap";
            }
        }

        return RelationKeyRules.Validate(parent, child) is { } violation ? $"key rule violation {violation}" : null;
    }
}
