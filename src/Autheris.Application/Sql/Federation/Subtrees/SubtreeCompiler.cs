namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Compiles same-source AST subtrees and co-located SQL Server subtrees via governed compilation,
/// preserving outer-join member row filters, isolated ordinal parameter binding, and masking heritage.
/// </summary>
public static class SubtreeCompiler
{
    private const int MaxAstTraversalDepth = 128;

    private static readonly SqlTokenSecurityOptions SubtreeRowFilterTokenOptions = new()
    {
        RejectComments = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = true,
        RejectTimeTravelQueries = true,
        RejectVariableSubstitutionSequences = true
    };

    /// <summary>Asserts that member row filters do not reference tables or columns in other databases.</summary>
    public static void ValidatePolicyIsolation(
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var table in tables)
        {
            var id = table.Metadata.Identifier;
            if (!decisions.TryGetValue(id, out var dec) || dec == null || string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql))
                continue;

            var currentDb = table.Metadata.Table.PhysicalDatabaseName ?? id.Domain;
            var externalTables = tables.Where(o => !string.Equals(
                currentDb, o.Metadata.Table.PhysicalDatabaseName ?? o.Metadata.Identifier.Domain, StringComparison.OrdinalIgnoreCase)).ToList();
            if (externalTables.Count == 0) continue;

            Expression expr;
            try
            {
                var (tree, _) = FastSqlEngine.Default.ParseExpression(NormalizeRowFilterForAst(dec.CombinedRowFilterSql).AsMemory(), SubtreeRowFilterTokenOptions);
                expr = new SqlAstBuilder(new AstBuilderOptions
                {
                    EnforceReadOnlyQueries = true,
                    EnforceFunctionPolicy = true,
                    AllowedFunctions = SqlDialectMapper.AllSafeDialectFunctions,
                    RejectTimeTravelQueries = true
                }).BuildStandaloneExpression(tree);
            }
            catch (GatewaySecurityException) { throw; }
            catch (Exception ex)
            {
                throw new GatewaySecurityException($"Row filter policy on '{id}' could not be parsed safely during isolation validation.", GovernedSqlErrorCodes.PolicyDenied, ex);
            }

            ValidateExpressionPolicyIsolation(expr, id, table, currentDb, externalTables);
        }
    }

    /// <summary>True only when every column of the table is Clear for its own decision; fails closed otherwise.</summary>
    internal static bool IsPushdownEligible(ResolvedSourceTable? table, TableAccessDecision? decision)
    {
        if (table?.Metadata is not { } metadata || decision is null || !decision.IsAllowed || !decision.HasUnconstrainedColumnAllow
            || decision.ScopeClearance is not null || metadata.ColumnMaskingRules.Count != 0
            || decision.ColumnMasks is { MaskedColumns.Count: > 0 })
        {
            return false;
        }

        foreach (var level in decision.ColumnAccess.Values)
            if (level != ColumnAccessLevel.Clear) return false;

        foreach (var column in metadata.Columns)
            if (decision.GetEffectiveColumnAccess(column.ColumnName, metadata) != ColumnAccessLevel.Clear) return false;

        return metadata.Columns.Count > 0 || decision.ColumnAccess.Count == 0;
    }

    public static SubtreeExecutionPlan Compile(
        SourceSubtree subtree,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        ISqlEngine sqlEngine,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subtree);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(tenantId.Value))
            throw new ArgumentException("A subtree can only be compiled for a tenant.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sqlEngine);

        // 1. Universal Authorization check (fail-closed)
        var memberDecisions = new Dictionary<TableIdentifier, TableAccessDecision>();
        foreach (var member in subtree.MemberTables)
        {
            var dec = CrossSourcePlanner.RequireDecision(decisions, member);
            if (!dec.IsAllowed)
                throw new WebSqlTableDeniedException($"Zero-Trust Violation: Access denied to table '{member.Target.FullName}'. Denied reasons: {string.Join("; ", dec.DeniedReasons)}");
            memberDecisions[member.Metadata.Identifier] = dec;
            if (!IsPushdownEligible(member, dec))
                throw new GatewaySecurityException($"Table '{member.Target.FullName}' has column restrictions and cannot be part of a pushed-down subtree.", GovernedSqlErrorCodes.PolicyDenied);
        }

        // 2. Validate function allowlists for target dialect
        var dialect = subtree.Dialect;
        var allowlist = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.GetDefault(dialect));
        foreach (var func in CollectFunctionCalls(subtree))
            if (!allowlist.Contains(func))
                throw new WebSqlPolicyException($"Function '{func}' is not permitted in {dialect} queries.");

        // 3. Pushable query predicates + per-member typed/normalized RLS predicates (preserves outer-join semantics)
        Expression? combinedWhere = null;
        foreach (var pred in subtree.PushablePredicates)
            combinedWhere = combinedWhere == null ? pred : new BinaryExpression(combinedWhere, BinaryOperator.And, pred);

        var policyAllowlist = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.BuildForPolicy(dialect, null));
        var memberRowPolicies = BuildMemberRowPolicies(subtree, memberDecisions, sqlEngine, policyAllowlist, ct);

        // 4. Assemble isolated QuerySpecification AST & emit canonical ANSI SQL
        var querySpec = new QuerySpecification(false, subtree.ProjectedItems, subtree.RootTableSource, combinedWhere, null, null);
        var ansiGenerator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Ansi);
        var canonicalSql = ansiGenerator.GenerateSql(new SelectStatement(null, querySpec, null, null));

        // 5. Governed Compilation via ISqlEngine.Compile (AstSecurityVisitor injects member RLS into derived table subqueries)
        var entries = subtree.MemberTables
            .Select(m => new TableCatalogEntry(
                new TableIdentity(m.Metadata.Table.SchemaName, m.Metadata.Table.TableName, null),
                m.Metadata.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
                m.Metadata.TenantColumnName, 0))
            .DistinctBy(e => e.Identity)
            .ToList();

        var policy = new GovernancePolicy
        {
            RowFilters = new SubtreeRowPolicyProvider(memberRowPolicies),
            Masks = new SubtreeNoOpMaskProvider(),
            Catalog = new InMemoryTableCatalog(entries, "public"),
            Tenant = new TenantBinding("__autheris_tenant", tenantId.Value, SqlParameterType.String)
        };

        var compiled = sqlEngine.Compile(canonicalSql.AsMemory(), CompileRequestFactory.Create(
            policy, dialect, new WebSqlOptions(), isDml: false, enforcedMaxRows: 0, allowedFunctions: allowlist, translateDateFunctions: true), ct);

        // 6. Lexer-aware ordinal parameter isolation (@p_<SubtreeId>_<ordinal>)
        var (isolatedSql, isolatedParams) = IsolateCompiledParameters(compiled, subtree.SubtreeId, dialect);

        // 7. Transitive Masking Heritage & Synthetic TableMetadata
        var (projectedColNames, maskedColumns) = CollectMaskingHeritage(subtree, memberDecisions);
        var syntheticMetadata = new TableMetadata
        {
            Identifier = new TableIdentifier(subtree.SourceName, "public", subtree.SubtreeId),
            Table = new Table { TableName = subtree.SubtreeId, SchemaName = "public", DataSourceType = DataSourceType.Sql, SourceName = subtree.SourceName, IsActive = true },
            Columns = projectedColNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
            ColumnMaskingRules = maskedColumns.ToDictionary(mc => mc, _ => new MaskingRule { RuleType = "REDACT" })
        };

        return new SubtreeExecutionPlan(
            subtree.SubtreeId, subtree.SourceName, dialect, subtree.MemberTables, compiled,
            projectedColNames, maskedColumns.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            syntheticMetadata, isolatedParams, isolatedSql);
    }

    private static (List<string> Projected, HashSet<string> Masked) CollectMaskingHeritage(
        SourceSubtree subtree, Dictionary<TableIdentifier, TableAccessDecision> memberDecisions)
    {
        var maskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectedColNames = new List<string>(subtree.ProjectedItems.Count);
        foreach (var proj in subtree.ProjectedItems)
        {
            if (proj is ColumnSelectItem colSelect)
            {
                var outName = colSelect.Alias?.Value ?? (colSelect.Expression is ColumnReference c0 ? c0.Name.Parts[^1].Value : "col");
                projectedColNames.Add(outName);
                var refs = new List<ColumnReference>();
                CollectColumnReferences(colSelect.Expression, refs);
                foreach (var cr in refs)
                {
                    var colName = cr.Name.Parts[^1].Value;
                    var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;
                    foreach (var m in subtree.MemberTables)
                    {
                        if ((qual == null || MatchesTable(qual, m)) && m.Metadata.HasColumn(colName))
                        {
                            var dec = memberDecisions[m.Metadata.Identifier];
                            if (dec.GetEffectiveColumnAccess(colName, m.Metadata) == ColumnAccessLevel.Mask ||
                                (m.Metadata.ColumnMaskingRules != null && m.Metadata.ColumnMaskingRules.ContainsKey(colName)))
                                maskedColumns.Add(outName);
                        }
                    }
                }
            }
            else if (proj is WildcardSelectItem)
            {
                foreach (var m in subtree.MemberTables)
                {
                    var dec = memberDecisions[m.Metadata.Identifier];
                    foreach (var c in m.Metadata.Columns)
                    {
                        projectedColNames.Add(c.ColumnName);
                        if (dec.GetEffectiveColumnAccess(c.ColumnName, m.Metadata) == ColumnAccessLevel.Mask ||
                            (m.Metadata.ColumnMaskingRules != null && m.Metadata.ColumnMaskingRules.ContainsKey(c.ColumnName)))
                            maskedColumns.Add(c.ColumnName);
                    }
                }
            }
        }
        return (projectedColNames, maskedColumns);
    }

    private static Dictionary<TableIdentity, PolicyPredicate> BuildMemberRowPolicies(
        SourceSubtree subtree, IReadOnlyDictionary<TableIdentifier, TableAccessDecision> memberDecisions,
        ISqlEngine sqlEngine, IReadOnlySet<string> policyAllowlist, CancellationToken ct)
    {
        var policies = new Dictionary<TableIdentity, PolicyPredicate>();
        for (int idx = 0; idx < subtree.MemberTables.Count; idx++)
        {
            var member = subtree.MemberTables[idx];
            var tid = new TableIdentity(member.Metadata.Table.SchemaName, member.Metadata.Table.TableName, null);
            if (policies.ContainsKey(tid)) continue;

            var dec = memberDecisions[member.Metadata.Identifier];
            if (string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql)) continue;

            if (dec.RowPolicy is PolicyPredicateRowPolicy typedRowPolicy &&
                string.Equals(dec.CombinedRowFilterSql, typedRowPolicy.Project().Sql, StringComparison.Ordinal))
            {
                if (typedRowPolicy.Predicate.Parameters.Count == 0)
                {
                    policies[tid] = typedRowPolicy.Predicate;
                }
                else
                {
                    var renamer = new PrefixParameterRenamer(typedRowPolicy.Predicate.Parameters, $"__sub_rls_{idx}_");
                    policies[tid] = PolicyPredicate.Create((Expression)renamer.Visit(typedRowPolicy.Predicate.Expression), renamer.Values);
                }
                continue;
            }

            var (exprTree, _) = sqlEngine.ParseExpression(NormalizeRowFilterForAst(dec.CombinedRowFilterSql).AsMemory(), SubtreeRowFilterTokenOptions, ct);
            var parsedExpr = new SqlAstBuilder(new AstBuilderOptions
            {
                EnforceReadOnlyQueries = true, EnforceFunctionPolicy = true,
                AllowedFunctions = policyAllowlist, TranslateTrinoDateFunctions = true, RejectTimeTravelQueries = true
            }).BuildStandaloneExpression(exprTree);

            var shaper = new SubtreeRlsShaper(member, dec.RowFilterParameters, $"__sub_rls_{idx}_");
            policies[tid] = PolicyPredicate.Create((Expression)shaper.Visit(parsedExpr), shaper.Values);
        }
        return policies;
    }

    private static string NormalizeRowFilterForAst(string sql)
    {
        var sb = new StringBuilder(sql.Length + 16);
        int i = 0;
        while (i < sql.Length)
        {
            if (TrySkipLiteralOrComment(sql, i, allowBrackets: false, out int next))
            {
                sb.Append(sql, i, next - i); i = next; continue;
            }
            char c = sql[i];
            if (c == '[' && (i == 0 || sql[i - 1] is '.' or '(' or ',' or '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '|' or '!' || char.IsWhiteSpace(sql[i - 1])))
            {
                int end = ScanDelimited(sql, i + 1, ']', out var ident);
                sb.Append('"').Append(ident.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                i = end; continue;
            }
            if (c == '@' && i + 1 < sql.Length && IsIdentStart(sql[i + 1]) && (i == 0 || !IsIdentContinue(sql[i - 1])))
            {
                int j = i + 2;
                while (j < sql.Length && IsIdentContinue(sql[j])) j++;
                sb.Append("__param_").Append(sql, i + 1, j - (i + 1));
                i = j; continue;
            }
            if (sql.AsSpan(i).StartsWith("__param_", StringComparison.OrdinalIgnoreCase) && (i == 0 || !IsIdentContinue(sql[i - 1])))
                throw new GatewaySecurityException("Row filter SQL contains a reserved parameter identifier prefix.", GovernedSqlErrorCodes.PolicyDenied);
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static (string Sql, Dictionary<string, object?> Parameters) IsolateCompiledParameters(CompiledSql compiled, string subtreeId, TargetSqlDialect dialect)
    {
        compiled.EnsureNoZoneArguments();
        var caps = DialectCapabilityTable.Default.Get(dialect);
        var byMarker = new Dictionary<string, (BoundParameter Param, int Ordinal)>(StringComparer.Ordinal);
        for (int idx = 0; idx < compiled.Parameters.Length; idx++)
        {
            var p = compiled.Parameters[idx];
            if (p.Ordinal != idx || string.IsNullOrEmpty(p.Marker) || !byMarker.TryAdd(p.Marker, (p, idx)))
                throw new GatewaySecurityException("Compiled subtree parameter metadata is misaligned.", GovernedSqlErrorCodes.PolicyDenied);
        }

        var isolatedParams = new Dictionary<string, object?>();
        var seenOrdinals = new HashSet<int>();
        var sql = compiled.Sql;
        var sb = new StringBuilder(sql.Length + 32);
        int i = 0;
        while (i < sql.Length)
        {
            if (TrySkipLiteralOrComment(sql, i, caps.IdentifierOpenQuote == '[', out int next))
            {
                sb.Append(sql, i, next - i); i = next; continue;
            }
            if (TryReadMarkerToken(sql, i, caps.MarkerStyle, out int tokenEnd))
            {
                string markerToken = sql[i..tokenEnd];
                if (!byMarker.TryGetValue(markerToken, out var match))
                    throw new GatewaySecurityException($"Subtree SQL contains unexpected parameter marker '{markerToken}'.", GovernedSqlErrorCodes.PolicyDenied);
                seenOrdinals.Add(match.Ordinal);
                string isolatedMarker = string.Create(CultureInfo.InvariantCulture, $"@p_{subtreeId}_{match.Ordinal}");
                isolatedParams[isolatedMarker] = match.Param.Value;
                sb.Append(isolatedMarker);
                i = tokenEnd; continue;
            }
            sb.Append(sql[i++]);
        }

        if (seenOrdinals.Count != compiled.Parameters.Length)
            throw new GatewaySecurityException("Subtree compiled parameters do not align with emitted SQL markers.", GovernedSqlErrorCodes.PolicyDenied);

        return (sb.ToString(), isolatedParams);
    }

    private static bool TrySkipLiteralOrComment(string sql, int i, bool allowBrackets, out int next)
    {
        char c = sql[i];
        if (c is '\'' or '"' or '`') { next = ScanDelimited(sql, i + 1, c, out _); return true; }
        if (c == '[' && allowBrackets) { next = ScanDelimited(sql, i + 1, ']', out _); return true; }
        if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
        {
            int lf = sql.AsSpan(i + 2).IndexOf('\n');
            next = lf >= 0 ? i + 2 + lf + 1 : sql.Length;
            return true;
        }
        if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
        {
            int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
            next = end >= 0 ? end + 2 : sql.Length;
            return true;
        }
        if (c == '$' && i + 1 < sql.Length && !char.IsAsciiDigit(sql[i + 1]))
        {
            int k = i + 1;
            while (k < sql.Length && IsIdentContinue(sql[k])) k++;
            if (k < sql.Length && sql[k] == '$')
            {
                int close = sql.IndexOf(sql[i..(k + 1)], k + 1, StringComparison.Ordinal);
                next = close >= 0 ? close + (k - i + 1) : sql.Length;
                return true;
            }
        }
        next = i;
        return false;
    }

    private static int ScanDelimited(string sql, int i, char close, out string content)
    {
        var sb = close == ']' ? new StringBuilder() : null;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { sb?.Append(close); i += 2; continue; }
                content = sb?.ToString() ?? string.Empty;
                return i + 1;
            }
            sb?.Append(sql[i]);
            i++;
        }
        if (close == ']') throw new GatewaySecurityException("Unterminated bracket identifier in SQL.", GovernedSqlErrorCodes.PolicyDenied);
        content = string.Empty;
        return sql.Length;
    }

    private static bool TryReadMarkerToken(string sql, int i, ParameterMarkerStyle style, out int end)
    {
        end = i;
        if (i > 0 && IsIdentContinue(sql[i - 1])) return false;
        char c = sql[i];
        switch (style)
        {
            case ParameterMarkerStyle.AtNamedOrdinal when c == '@':
                if (i + 1 >= sql.Length || sql[i + 1] == '@' || !IsIdentStart(sql[i + 1]))
                    throw new GatewaySecurityException("Invalid '@' parameter marker in compiled subtree SQL.", GovernedSqlErrorCodes.PolicyDenied);
                end = i + 2; while (end < sql.Length && IsIdentContinue(sql[end])) end++;
                return true;
            case ParameterMarkerStyle.DollarOrdinal when c == '$' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]):
                end = i + 2; while (end < sql.Length && char.IsAsciiDigit(sql[end])) end++;
                if (end < sql.Length && IsIdentContinue(sql[end]))
                    throw new GatewaySecurityException("Malformed '$' parameter marker in compiled subtree SQL.", GovernedSqlErrorCodes.PolicyDenied);
                return true;
            case ParameterMarkerStyle.QuestionOrdinal when c == '?':
                if (i + 1 >= sql.Length || !char.IsAsciiDigit(sql[i + 1]))
                    throw new GatewaySecurityException("Unexpected positional '?' marker in compiled subtree SQL.", GovernedSqlErrorCodes.PolicyDenied);
                end = i + 2; while (end < sql.Length && char.IsAsciiDigit(sql[end])) end++;
                return true;
            case ParameterMarkerStyle.ColonNamedOrdinal or ParameterMarkerStyle.ColonOrdinal when c == ':':
                if ((i > 0 && sql[i - 1] == ':') || (i + 1 < sql.Length && sql[i + 1] is ':' or '=') || i + 1 >= sql.Length || !IsIdentContinue(sql[i + 1]))
                    return false;
                end = i + 2; while (end < sql.Length && IsIdentContinue(sql[end])) end++;
                return true;
            default:
                return false;
        }
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentContinue(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static void ValidateExpressionPolicyIsolation(
        Expression expr, TableIdentifier currentId, ResolvedSourceTable currentTable, string currentDb, List<ResolvedSourceTable> externalTables)
    {
        var baseAliases = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(currentTable.Target.Alias)) baseAliases.Add(currentTable.Target.Alias);
        baseAliases.Add(currentTable.Target.TableName);
        baseAliases.Add(currentTable.Metadata.Table.TableName);
        baseAliases.Add(currentTable.Metadata.Identifier.TableName);
        baseAliases.Add(RowFilterAliases.Target);

        var stack = new Stack<(SqlNode Node, ImmutableHashSet<string> Aliases, int Depth)>();
        stack.Push((expr, baseAliases.ToImmutable(), 0));
        while (stack.Count > 0)
        {
            var (node, aliases, depth) = stack.Pop();
            if (depth > MaxAstTraversalDepth)
                throw new GatewaySecurityException($"Row filter policy on '{currentId}' exceeds maximum AST depth.", GovernedSqlErrorCodes.PolicyDenied);

            var childAliases = aliases;
            if (node is QuerySpecification qs && qs.From != null) childAliases = aliases.Union(CollectTableSourceAliases(qs.From));
            else if (node is NamedTableSource nts) CheckExternalParts(nts.Name.Parts, isColumn: false, currentId, currentTable, currentDb, aliases, externalTables);
            else if (node is TableQueryBody tqb) CheckExternalParts(tqb.TableName.Parts, isColumn: false, currentId, currentTable, currentDb, aliases, externalTables);
            else if (node is ColumnReference cr && cr.Name.Parts.Count > 1) CheckExternalParts(cr.Name.Parts, isColumn: true, currentId, currentTable, currentDb, aliases, externalTables);

            foreach (var child in node.EnumerateChildren()) stack.Push((child, childAliases, depth + 1));
        }
    }

    private static void CheckExternalParts(
        IReadOnlyList<SqlIdentifier> parts, bool isColumn, TableIdentifier currentId, ResolvedSourceTable currentTable,
        string currentDb, ImmutableHashSet<string> localAliases, List<ResolvedSourceTable> externalTables)
    {
        int scopeCount = isColumn ? parts.Count - 1 : parts.Count;
        bool hasExplicitCurrentDb = scopeCount >= 3 && string.Equals(parts[0].Value, currentDb, StringComparison.OrdinalIgnoreCase);

        foreach (var other in externalTables)
        {
            var otherDb = other.Metadata.Table.PhysicalDatabaseName ?? other.Metadata.Identifier.Domain;
            for (int q = 0; q < parts.Count - 1; q++)
                if (string.Equals(parts[q].Value, otherDb, StringComparison.OrdinalIgnoreCase))
                    throw ExternalPolicyDenied(currentId, otherDb);

            if (hasExplicitCurrentDb) continue;
            string targetName = parts[scopeCount - 1].Value;
            if (isColumn && localAliases.Contains(targetName)) continue;

            bool matchesOther = string.Equals(targetName, other.Metadata.Identifier.TableName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(targetName, other.Target.TableName, StringComparison.OrdinalIgnoreCase);
            bool matchesCurrent = string.Equals(targetName, currentTable.Metadata.Identifier.TableName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(targetName, currentTable.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(targetName, currentTable.Metadata.Table.TableName, StringComparison.OrdinalIgnoreCase);

            if (matchesOther && (isColumn || !matchesCurrent)) throw ExternalPolicyDenied(currentId, otherDb);
        }
    }

    private static GatewaySecurityException ExternalPolicyDenied(TableIdentifier currentId, string otherDb) =>
        new($"Cross-database correlated row filter subqueries are not permitted; policy on '{currentId}' references external table or database '{otherDb}'.", GovernedSqlErrorCodes.PolicyDenied);

    private static HashSet<string> CollectTableSourceAliases(TableSource from, bool includeUnaliasedTable = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(TableSource Node, int Depth)>();
        stack.Push((from, 0));
        while (stack.Count > 0)
        {
            var (ts, depth) = stack.Pop();
            if (depth > MaxAstTraversalDepth) throw new WebSqlPolicyException("Table source AST exceeds maximum traversal depth.");
            switch (ts)
            {
                case NamedTableSource nts when nts.Alias != null: result.Add(nts.Alias.Value); break;
                case NamedTableSource nts when includeUnaliasedTable: result.Add(nts.Name.SimpleName); break;
                case SubqueryTableSource sq when sq.Alias != null: result.Add(sq.Alias.Value); break;
                case JoinedTableSource j: stack.Push((j.Right, depth + 1)); stack.Push((j.Left, depth + 1)); break;
            }
        }
        return result;
    }

    private static HashSet<string> CollectFunctionCalls(SourceSubtree subtree)
    {
        var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(SqlNode Node, int Depth)>();
        foreach (var p in subtree.PushablePredicates) stack.Push((p, 0));
        foreach (var item in subtree.ProjectedItems) stack.Push((item, 0));
        stack.Push((subtree.RootTableSource, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MaxAstTraversalDepth) throw new WebSqlPolicyException("Subtree query AST exceeds maximum traversal depth.");
            if (node is FunctionCallExpression func) functions.Add(string.Join(".", func.Name.Parts.Select(p => p.Value)));
            foreach (var child in node.EnumerateChildren()) stack.Push((child, depth + 1));
        }
        return functions;
    }

    private static void CollectColumnReferences(SqlNode root, List<ColumnReference> list)
    {
        var stack = new Stack<(SqlNode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MaxAstTraversalDepth) throw new WebSqlPolicyException("Subtree expression AST exceeds maximum traversal depth.");
            if (node is ColumnReference cr) { list.Add(cr); continue; }
            foreach (var child in node.EnumerateChildren()) stack.Push((child, depth + 1));
        }
    }

    private static bool MatchesTable(string qualifier, ResolvedSourceTable table) =>
        string.Equals(qualifier, table.Target.Alias, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(qualifier, table.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(qualifier, table.Metadata.Table.TableName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(qualifier, table.Target.FullName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(qualifier, table.Metadata.Identifier.ToString(), StringComparison.OrdinalIgnoreCase);

    private sealed class SubtreeRlsShaper(
        ResolvedSourceTable member, IReadOnlyDictionary<string, object?>? rowFilterParameters, string paramPrefix) : SqlAstRewriter
    {
        private readonly Dictionary<string, PolicyValue> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PolicyParameterExpression> _boundMarkers = new(StringComparer.Ordinal);
        private readonly Stack<HashSet<string>> _subqueryAliases = new();
        private int _counter;

        public IReadOnlyDictionary<string, PolicyValue> Values => _values;

        public override SqlNode Visit(SqlNode node) => node switch
        {
            CurrentDateTimeExpression or TypedLiteralExpression or IntervalLiteralExpression or DateFunctionExpression or NamedBindMarkerExpression =>
                throw new GatewaySecurityException($"Subtree row filter on '{member.Target.FullName}' uses an unsupported expression node.", GovernedSqlErrorCodes.PolicyDenied),
            _ => base.Visit(node)
        };

        public override SqlNode VisitQuerySpecification(QuerySpecification node)
        {
            _subqueryAliases.Push(node.From != null ? CollectTableSourceAliases(node.From, includeUnaliasedTable: true) : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            try { return base.VisitQuerySpecification(node); }
            finally { _subqueryAliases.Pop(); }
        }

        public override SqlNode VisitExistsExpression(ExistsExpression node)
        {
            if (node.Subquery.Body is QuerySpecification { Projections: [ColumnSelectItem { Alias: null, Expression: LiteralExpression { Type: LiteralType.Integer, Value: { } v } }] } qs &&
                Convert.ToInt64(v, CultureInfo.InvariantCulture) == 1)
            {
                return node with
                {
                    Subquery = node.Subquery with
                    {
                        Body = qs with
                        {
                            From = qs.From != null ? (TableSource?)Visit(qs.From) : null,
                            Where = qs.Where != null ? (Expression)Visit(qs.Where) : null,
                            GroupBy = qs.GroupBy != null ? (GroupByClause)Visit(qs.GroupBy) : null,
                            Having = qs.Having != null ? (Expression)Visit(qs.Having) : null
                        }
                    }
                };
            }
            return base.VisitExistsExpression(node);
        }

        public override SqlNode VisitParameterReference(ParameterReference node)
        {
            if (!node.IsSynthetic) throw new GatewaySecurityException("Positional parameters are not supported in subtree row filters.", GovernedSqlErrorCodes.PolicyDenied);
            if (_boundMarkers.TryGetValue(node.Name, out var existing)) return existing;
            if (!TryResolveParam(node.Name, out var rawValue))
                throw new GatewaySecurityException($"Subtree row filter on '{member.Target.FullName}' references unbound parameter '@{node.Name}'.", GovernedSqlErrorCodes.PolicyDenied);
            string name = string.Create(CultureInfo.InvariantCulture, $"{paramPrefix}{_counter++}");
            var policyVal = CreatePolicyValue(rawValue);
            _values[name] = policyVal;
            return _boundMarkers[node.Name] = new PolicyParameterExpression(name, policyVal.Type, ParameterOrigin.Policy);
        }

        public override SqlNode VisitLikeExpression(LikeExpression node) => node with
        {
            Operand = (Expression)Visit(node.Operand),
            Pattern = node.Pattern switch
            {
                LiteralExpression { Type: LiteralType.String, Value: { } s } => AllocParam(s.ToString() ?? string.Empty, SqlParameterType.String, isLikePattern: true),
                ParameterReference pr => (Expression)VisitParameterReference(pr),
                _ => (Expression)Visit(node.Pattern)
            },
            Escape = node.Escape != null ? (Expression)Visit(node.Escape) : null
        };

        public override SqlNode VisitBinaryExpression(BinaryExpression node) =>
            node.Operator == BinaryOperator.Equal &&
            node.Left is LiteralExpression { Type: LiteralType.Integer, Value: { } l } &&
            node.Right is LiteralExpression { Type: LiteralType.Integer, Value: { } r } &&
            Convert.ToInt64(l, CultureInfo.InvariantCulture) == 1 && Convert.ToInt64(r, CultureInfo.InvariantCulture) is 0 or 1
                ? node : base.VisitBinaryExpression(node);

        public override SqlNode VisitLiteralExpression(LiteralExpression node) => node.Type switch
        {
            LiteralType.Null or LiteralType.Boolean => node,
            LiteralType.Integer => Convert.ToInt64(node.Value, CultureInfo.InvariantCulture) is var v && v is >= int.MinValue and <= int.MaxValue
                ? AllocParam((int)v, SqlParameterType.Int32) : AllocParam(v, SqlParameterType.Int64),
            LiteralType.Decimal => AllocParam(decimal.Parse(Convert.ToString(node.Value, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture), SqlParameterType.Decimal),
            LiteralType.String => AllocParam(node.Value?.ToString() ?? string.Empty, SqlParameterType.String),
            _ => throw new GatewaySecurityException($"Subtree row filter on '{member.Target.FullName}' contains unsupported literal '{node.Type}'.", GovernedSqlErrorCodes.PolicyDenied)
        };

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            var parts = node.Name.Parts;
            string rawCol = parts[^1].Value;
            string col = member.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, rawCol, StringComparison.OrdinalIgnoreCase))?.ColumnName ?? rawCol;

            if (_subqueryAliases.Count == 0)
            {
                if (parts.Count == 1 || MatchesMemberQualifier(parts))
                    return new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier(col, parts[^1].IsQuoted)]));
                throw new GatewaySecurityException($"Subtree row filter on '{member.Target.FullName}' references foreign column '{node.Name}'.", GovernedSqlErrorCodes.PolicyDenied);
            }

            return parts.Count >= 2 && MatchesMemberQualifier(parts) && !_subqueryAliases.Any(s => s.Contains(parts[0].Value))
                ? new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier(col, parts[^1].IsQuoted)]))
                : node;
        }

        private PolicyParameterExpression AllocParam(object value, SqlParameterType type, bool isLikePattern = false)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"{paramPrefix}{_counter++}");
            _values[name] = new PolicyValue(value, type);
            return new PolicyParameterExpression(name, type, ParameterOrigin.Policy, isLikePattern);
        }

        private bool TryResolveParam(string bareName, out object? value)
        {
            value = null;
            if (rowFilterParameters is null) return false;
            string atName = "@" + bareName;
            if (rowFilterParameters.TryGetValue(atName, out value) || rowFilterParameters.TryGetValue(bareName, out value)) return true;
            foreach (var kv in rowFilterParameters)
                if (string.Equals(kv.Key, atName, StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, bareName, StringComparison.OrdinalIgnoreCase))
                { value = kv.Value; return true; }
            return false;
        }

        private bool MatchesMemberQualifier(IReadOnlyList<SqlIdentifier> parts)
        {
            string q = parts.Count == 2 ? parts[0].Value : string.Join(".", parts.Take(parts.Count - 1).Select(p => p.Value));
            return string.Equals(q, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) || MatchesTable(q, member) || MatchesTable(parts[^2].Value, member);
        }

        private static PolicyValue CreatePolicyValue(object? raw) => raw switch
        {
            null => new PolicyValue(null, SqlParameterType.Null),
            bool b => new PolicyValue(b, SqlParameterType.Boolean),
            byte or sbyte or short or ushort or int => new PolicyValue(Convert.ToInt32(raw, CultureInfo.InvariantCulture), SqlParameterType.Int32),
            uint or long => new PolicyValue(Convert.ToInt64(raw, CultureInfo.InvariantCulture), SqlParameterType.Int64),
            decimal d => new PolicyValue(d, SqlParameterType.Decimal),
            float or double => new PolicyValue(Convert.ToDouble(raw, CultureInfo.InvariantCulture), SqlParameterType.Double),
            DateOnly dt => new PolicyValue(dt, SqlParameterType.Date),
            DateTimeOffset dto => new PolicyValue(dto, SqlParameterType.TimestampTz),
            DateTime dt => new PolicyValue(dt, SqlParameterType.Timestamp),
            TimeOnly t => new PolicyValue(t, SqlParameterType.Time),
            byte[] bytes => new PolicyValue(bytes, SqlParameterType.Binary),
            Guid g => new PolicyValue(g.ToString(), SqlParameterType.String),
            _ => new PolicyValue(raw.ToString() ?? string.Empty, SqlParameterType.String)
        };
    }

    private sealed class PrefixParameterRenamer(IReadOnlyDictionary<string, PolicyValue> originalValues, string prefix) : SqlAstRewriter
    {
        private readonly Dictionary<string, PolicyValue> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PolicyParameterExpression> _renamed = new(StringComparer.Ordinal);
        private int _counter;

        public IReadOnlyDictionary<string, PolicyValue> Values => _values;

        public override SqlNode Visit(SqlNode node)
        {
            if (node is not PolicyParameterExpression param) return base.Visit(node);
            if (_renamed.TryGetValue(param.Name, out var existing)) return existing;
            if (!originalValues.TryGetValue(param.Name, out var val))
                throw new GatewaySecurityException($"Subtree row policy parameter '{param.Name}' has no bound value.", GovernedSqlErrorCodes.PolicyDenied);
            string newName = string.Create(CultureInfo.InvariantCulture, $"{prefix}{_counter++}");
            _values[newName] = val;
            return _renamed[param.Name] = param with { Name = newName };
        }
    }

    private sealed class SubtreeRowPolicyProvider(IReadOnlyDictionary<TableIdentity, PolicyPredicate> predicates) : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
    }

    private sealed class SubtreeNoOpMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) =>
            throw new InvalidOperationException("No masks configured on subtree compile pass.");
    }
}
