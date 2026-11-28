namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

/// <summary>
/// The closed set of statement-structure constructs a rejection may name. Every label is a constant of this type; nothing derived from
/// an exception message, a policy or a client literal can be added. Expression-level constructs (casts, literals, intervals, masks,
/// policy predicates) are deliberately absent: a row policy or a mask can contain them.
/// </summary>
public static class SqlConstructCatalog
{
    public const string TableSample = "TABLESAMPLE";
    public const string Pivot = "PIVOT";
    public const string MatchRecognize = "MATCH_RECOGNIZE";
    public const string Corresponding = "CORRESPONDING";
    public const string WindowClause = "WINDOW clause";
    public const string GroupsWindowFrame = "GROUPS window frame";
    public const string NamedWindowReference = "named window reference";
    public const string WindowInheritance = "window inheritance";
    public const string NullTreatment = "IGNORE NULLS / RESPECT NULLS";
    public const string RunningOrFinal = "RUNNING / FINAL";
    public const string NamedFunctionArguments = "named function arguments";
    public const string RowFieldNames = "ROW field names";
    public const string TableBranch = "table branch";
    public const string GroupByAll = "GROUP BY ALL";
    public const string Lateral = "LATERAL";
    public const string WithRecursive = "WITH RECURSIVE";
    public const string CteSelfReference = "CTE self-reference without RECURSIVE";
    public const string FetchWithTies = "FETCH ... WITH TIES";
    public const string JoinUsing = "JOIN ... USING";
    public const string IntersectMixed = "INTERSECT mixed with UNION or EXCEPT";

    private static readonly string[] NoAnchor = [];

    /// <summary>Label to the upper-case keyword sequence that locates it in the client text; empty when the construct has no keyword.</summary>
    public static IReadOnlyDictionary<string, string[]> Anchors { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [TableSample] = [TableSample],
        [Pivot] = [Pivot],
        [MatchRecognize] = [MatchRecognize],
        [Corresponding] = [Corresponding],
        [WindowClause] = ["WINDOW"],
        [GroupsWindowFrame] = ["GROUPS"],
        [NamedWindowReference] = NoAnchor,
        [WindowInheritance] = NoAnchor,
        [NullTreatment] = NoAnchor,
        [RunningOrFinal] = NoAnchor,
        [NamedFunctionArguments] = NoAnchor,
        [RowFieldNames] = NoAnchor,
        [TableBranch] = NoAnchor,
        [GroupByAll] = ["GROUP", "BY", "ALL"],
        [Lateral] = [Lateral],
        [WithRecursive] = ["WITH", "RECURSIVE"],
        [CteSelfReference] = NoAnchor,
        [FetchWithTies] = ["WITH", "TIES"],
        [JoinUsing] = ["USING"],
        [IntersectMixed] = ["INTERSECT"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>True when <paramref name="label"/> is exactly one of the catalogued labels.</summary>
    public static bool IsPublic(string? label) => label is not null && Anchors.ContainsKey(label);
}
