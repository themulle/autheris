namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Issue #107 (PLAN-GOV-107 C-7, A-3): turns a registered correlated <c>EXISTS</c> policy node into an equivalent, governed key
/// query (<c>SELECT DISTINCT dep.pk ... WHERE dep.pk IS NOT NULL AND ...</c>). The key query is governed by the same
/// <c>PolicySubqueryTenantRewriter</c> as the <c>EXISTS</c> body, so the tenant wrapper of every physical table is guaranteed.
/// </summary>
public interface IPolicyKeyQueryCompiler
{
    /// <exception cref="KeyQueryNotDerivableException">The <c>EXISTS</c> cannot be turned into an equivalent key query.</exception>
    CompiledKeyQuery CompileKeyQuery(PolicyKeyQueryRequest request, CancellationToken ct);
}

/// <summary>Input of <see cref="IPolicyKeyQueryCompiler"/>. <paramref name="RowLimit"/> is <c>MaxKeys + 1</c> (overflow detection).</summary>
public sealed record PolicyKeyQueryRequest(
    ExistsExpression Correlated,
    IReadOnlyDictionary<string, PolicyValue> Parameters,
    ITableCatalog Catalog,
    TableCatalogEntry Target,
    TenantBinding Tenant,
    TargetSqlDialect Dialect,
    int RowLimit)
{
    // SEC-RF107-09: parameter values never appear in text output.
    public override string ToString() => $"PolicyKeyQueryRequest {{ Target = {Target.Identity}, Dialect = {Dialect}, RowLimit = {RowLimit}, Parameters = {Parameters.Count} }}";
}

/// <summary>A compiled key query. Bind values never appear in <see cref="ToString"/> (SEC-RF107-09, INV-16).</summary>
public sealed record CompiledKeyQuery(
    string Sql,
    ImmutableArray<BoundParameter> Parameters,
    string? KeyColumnType,
    string? ForeignKeyColumnType,
    string ForeignKeyColumn,
    string CompilerVersion)
{
    public override string ToString() =>
        $"CompiledKeyQuery {{ ForeignKeyColumn = {ForeignKeyColumn}, KeyColumnType = {KeyColumnType}, ForeignKeyColumnType = {ForeignKeyColumnType}, Parameters = {Parameters.Length}, CompilerVersion = {CompilerVersion} }}";
}
