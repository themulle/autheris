namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Issue #123 (WP-123-1): AST subtree representing a maximal contiguous join subgraph
/// residing on the exact same physical SQL data source.
/// </summary>
public sealed record SourceSubtree(
    string SubtreeId,
    string SourceName,
    TargetSqlDialect Dialect,
    IReadOnlyList<ResolvedSourceTable> MemberTables,
    TableSource RootTableSource,
    IReadOnlyList<Expression> PushablePredicates,
    IReadOnlyList<SelectItem> ProjectedItems);

/// <summary>
/// Issue #123 (WP-123-2): Governed execution plan for a compiled native SQL subtree.
/// </summary>
public sealed record SubtreeExecutionPlan(
    string SubtreeId,
    string SourceName,
    TargetSqlDialect Dialect,
    IReadOnlyList<ResolvedSourceTable> MemberTables,
    CompiledSql Compiled,
    IReadOnlyList<string> ProjectedColumns,
    IReadOnlySet<string> MaskedColumns,
    TableMetadata SyntheticMetadata,
    IReadOnlyDictionary<string, object?>? Parameters = null);

/// <summary>
/// Issue #123 (WP-123-1): Result of partitioning query AST into same-source subtrees and remaining single tables.
/// </summary>
public sealed record SubtreePartitionResult(
    IReadOnlyList<SourceSubtree> Subtrees,
    IReadOnlyList<ResolvedSourceTable> RemainingSingleTables);
