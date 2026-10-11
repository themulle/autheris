namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>An admin-authored policy predicate is invalid. Fail closed: the table is denied. Messages never echo policy text.</summary>
public sealed class PolicyParseException : SecurityException
{
    public PolicyParseException(string message) : base(message)
    {
    }
}

public interface IPolicyExpressionParser
{
    /// <summary>Parses canonical (Trino) policy text once; cached; throws <see cref="PolicyParseException"/> (fail closed).</summary>
    PolicyPredicate Parse(string policySql, PolicyParseContext context);
}

/// <param name="Table">The secured table.</param>
/// <param name="AllowedColumns">Catalog columns of the secured table (canonical spelling). Anything else is rejected.</param>
/// <param name="AllowedFunctions">Allow-listed function names (case-insensitive).</param>
/// <param name="Parameters">Declared named parameters (<c>:name</c>) with their current values and types.</param>
/// <param name="CatalogVersion">Part of the cache key.</param>
public sealed record PolicyParseContext(
    TableIdentity Table,
    IReadOnlyList<string> AllowedColumns,
    IReadOnlySet<string> AllowedFunctions,
    IReadOnlyDictionary<string, PolicyValue> Parameters,
    long CatalogVersion)
{
    /// <summary>Catalog for correlated subqueries; without it every subquery is rejected.</summary>
    public ITableCatalog? Catalog { get; init; }

    /// <summary>Cache partition (for example the tenant): one partition cannot evict another's entries.</summary>
    public string? CachePartition { get; init; }

    /// <summary>
    /// The dialect the predicate will run on, when known. For Oracle the parser rejects forms in which an empty string (which is
    /// NULL there) could loosen the predicate (SEC-ADG-17 item 2, CR-ADG-18).
    /// </summary>
    public TargetSqlDialect? TargetDialect { get; init; }
}

public sealed class PolicyParseCacheOptions
{
    public int MaxEntries { get; init; } = 4096;
    public int MaxEntriesPerPartition { get; init; } = 256;
    public TimeSpan NegativeTtl { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record PolicyParseCacheStats(long Hits, long Misses, long NegativeHits, int Entries);

/// <summary>
/// Parses admin-authored policy predicates (canonical Trino syntax) into typed IR (plan 3.5, SEC-ADG-11, -17, -18, -20).
/// Every literal and named marker becomes a <see cref="PolicyParameterExpression"/>, columns resolve to the catalog's canonical
/// spelling, functions must be allow-listed and deterministic, and subqueries may only reference catalog tables, one level deep.
/// </summary>
public sealed class PolicyExpressionParser : IPolicyExpressionParser
{
    public const int MaxPolicyTextLength = 4096;
    public const int MaxNodes = 256;

    private static readonly HashSet<string> NondeterministicOrSessionFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "random", "rand", "now", "uuid", "newid", "newsequentialid", "getdate", "getutcdate", "sysdatetime", "sysdatetimeoffset",
        "current_user", "session_user", "user", "system_user", "suser_name", "suser_sname", "user_name", "current_timestamp",
        "current_date", "current_time", "localtime", "localtimestamp", "current_catalog", "current_schema", "current_path",
        "host_name", "app_name", "context_info", "session_context", "sys_context", "connection_id"
    };

    private readonly ISqlEngine _engine;
    private readonly TimeProvider _time;
    private readonly PolicyParseCacheOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, CacheEntry>> _partitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _order = new(StringComparer.Ordinal);
    private int _entries;
    private long _hits, _misses, _negativeHits;

    public PolicyExpressionParser(ISqlEngine engine, TimeProvider? timeProvider = null, PolicyParseCacheOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new PolicyParseCacheOptions();
    }

    public PolicyParseCacheStats CacheStats
    {
        get
        {
            lock (_gate)
            {
                return new PolicyParseCacheStats(_hits, _misses, _negativeHits, _entries);
            }
        }
    }

    /// <summary>
    /// SHA-256 over the policy text, table identity, catalog version, allowed columns, allowed function set, declared parameter
    /// names and types, and the compiler version. Parameter values are not part of the key.
    /// </summary>
    public static string ComputeCacheKey(string policySql, PolicyParseContext context, string compilerVersion)
    {
        var sb = new StringBuilder();
        sb.Append(policySql.Length).Append(':').Append(policySql).Append('|');
        sb.Append(context.Table).Append('|').Append(context.CatalogVersion.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(context.Catalog is null ? "nocat" : "cat").Append('|');
        foreach (var c in context.AllowedColumns.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(c).Append(',');
        sb.Append('|');
        foreach (var f in context.AllowedFunctions.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)) sb.Append(f).Append(',');
        sb.Append('|');
        foreach (var p in context.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(p.Key).Append('=').Append((int)p.Value.Type).Append(',');
        sb.Append('|').Append(context.TargetDialect?.ToString() ?? "-");
        sb.Append('|').Append(compilerVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public PolicyPredicate Parse(string policySql, PolicyParseContext context)
    {
        ArgumentNullException.ThrowIfNull(policySql);
        ArgumentNullException.ThrowIfNull(context);
        if (policySql.Length is 0 or > MaxPolicyTextLength || string.IsNullOrWhiteSpace(policySql))
        {
            throw new PolicyParseException("The policy predicate is empty or too long.");
        }

        string key = ComputeCacheKey(policySql, context, CompilerInfo.Version);
        string partition = context.CachePartition ?? string.Empty;
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_partitions.TryGetValue(partition, out var entries) && entries.TryGetValue(key, out var entry))
            {
                if (entry.Shape is not null)
                {
                    _hits++;
                    return Instantiate(entry.Shape, context);
                }

                if (entry.ExpiresAt > now)
                {
                    _negativeHits++;
                    throw new PolicyParseException(entry.NegativeReason!);
                }

                entries.Remove(key);
                _entries--;
            }

            _misses++;
        }

        Shape shape;
        try
        {
            shape = ParseShape(policySql, context);
        }
        catch (PolicyParseException ex)
        {
            Store(partition, key, new CacheEntry(null, ex.Message, now + _options.NegativeTtl));
            throw;
        }
        catch (SecurityException ex)
        {
            // Token guards, the AST builder or the function policy rejected the text: a typed policy rejection.
            string reason = "The policy predicate was rejected by the SQL security guards: " + ex.GetType().Name + ".";
            Store(partition, key, new CacheEntry(null, reason, now + _options.NegativeTtl));
            throw new PolicyParseException(reason);
        }
        catch (Exception ex) when (ex is AstBuildException or NotSupportedException or Antlr4.Runtime.Misc.ParseCanceledException)
        {
            string reason = "The policy predicate is not valid canonical Trino SQL.";
            Store(partition, key, new CacheEntry(null, reason, now + _options.NegativeTtl));
            throw new PolicyParseException(reason);
        }

        Store(partition, key, new CacheEntry(shape, null, default));
        return Instantiate(shape, context);
    }

    private void Store(string partition, string key, CacheEntry entry)
    {
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partition, out var entries))
            {
                entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                _partitions[partition] = entries;
                _order[partition] = new Queue<string>();
            }

            if (!entries.ContainsKey(key))
            {
                _entries++;
                _order[partition].Enqueue(key);
            }

            entries[key] = entry;

            // Per-partition share first: a flood in one partition only evicts that partition's oldest entries.
            while (entries.Count > _options.MaxEntriesPerPartition && Evict(partition)) { }

            // Global bound: evict from the largest partition.
            while (_entries > _options.MaxEntries)
            {
                string? largest = _partitions.OrderByDescending(p => p.Value.Count).Select(p => p.Key).FirstOrDefault();
                if (largest is null || !Evict(largest)) break;
            }
        }
    }

    private bool Evict(string partition)
    {
        var queue = _order[partition];
        var entries = _partitions[partition];
        while (queue.Count > 0)
        {
            if (entries.Remove(queue.Dequeue()))
            {
                _entries--;
                return true;
            }
        }

        return false;
    }

    // ---- shape (value-free, cacheable) ----

    private sealed record CacheEntry(Shape? Shape, string? NegativeReason, DateTimeOffset ExpiresAt);

    private sealed record Shape(
        Expression Expression,
        FrozenDictionary<string, PolicyValue> LiteralValues,
        ImmutableArray<string> DeclaredNames,
        string Fingerprint,
        ImmutableHashSet<string> ReferencedColumns);

    private static PolicyPredicate Instantiate(Shape shape, PolicyParseContext context)
    {
        var values = new Dictionary<string, PolicyValue>(shape.LiteralValues, StringComparer.Ordinal);
        foreach (var name in shape.DeclaredNames)
        {
            // The cached shape is value-free: the values come from the current request's policy.
            if (!context.Parameters.TryGetValue(name, out var value))
            {
                throw new PolicyParseException("A declared policy parameter has no value.");
            }

            values[name] = value;
        }

        return new PolicyPredicate(shape.Expression, values.ToFrozenDictionary(StringComparer.Ordinal), shape.Fingerprint, shape.ReferencedColumns);
    }

    private Shape ParseShape(string policySql, PolicyParseContext context)
    {
        string text = SubstituteNamedMarkers(policySql);
        var tokenOptions = SqlTokenSecurityOptions.Strict;
        var (tree, _) = _engine.ParseExpression(text.AsMemory(), tokenOptions);

        var builderOptions = new AstBuilderOptions
        {
            EnforceReadOnlyQueries = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = context.AllowedFunctions,
            AllowedTableFunctions = null,
            TranslateTrinoDateFunctions = false,
            RejectTimeTravelQueries = true
        };
        var expression = new SqlAstBuilder(builderOptions).BuildStandaloneExpression(tree);

        string hash8 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policySql)))[..8].ToLowerInvariant();
        var shaper = new PolicyShaper(context, hash8);
        var shaped = (Expression)shaper.Visit(expression);
        if (context.TargetDialect == TargetSqlDialect.Oracle)
        {
            RejectEmptyStringSensitiveForms(shaped, context, shaper.LiteralValues);
        }

        return new Shape(
            shaped,
            shaper.LiteralValues.ToFrozenDictionary(StringComparer.Ordinal),
            shaper.DeclaredNames.ToImmutableArray(),
            AstFingerprint.Compute(shaped),
            AstReflection.ReferencedTargetColumns(shaped));
    }

    private static readonly HashSet<string> NullSensitiveFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "coalesce", "nvl", "nvl2", "nullif", "ifnull", "decode", "greatest", "least"
    };

    /// <summary>
    /// SEC-ADG-17 item 2: in Oracle the empty string is NULL. <c>NOT (col = :p)</c>, <c>CASE ... ELSE</c> and <c>COALESCE</c>
    /// over a string parameter turn into a looser predicate when the value is empty. The Oracle binder already rejects an empty
    /// tenant or policy string; this closes the same hole at parse time (defense in depth) because any string parameter can be
    /// empty by configuration.
    /// </summary>
    private static void RejectEmptyStringSensitiveForms(Expression shaped, PolicyParseContext context, IReadOnlyDictionary<string, PolicyValue> literals)
    {
        bool IsString(PolicyParameterExpression parameter) =>
            (literals.TryGetValue(parameter.Name, out var literal) && literal.Type == SqlParameterType.String) ||
            (context.Parameters.TryGetValue(parameter.Name, out var declared) && declared.Type == SqlParameterType.String) ||
            parameter.Type == SqlParameterType.String;

        bool ContainsStringParameter(object? subtree)
        {
            bool found = false;
            AstReflection.Walk(subtree, child =>
            {
                if (child is PolicyParameterExpression parameter && IsString(parameter)) found = true;
                return !found;
            });
            return found;
        }

        AstReflection.Walk(shaped, node =>
        {
            bool sensitive = node is CaseExpression ||
                node is UnaryExpression { Operator: UnaryOperator.Not } ||
                (node is FunctionCallExpression fn && NullSensitiveFunctions.Contains(fn.Name.SimpleName));
            if (sensitive && ContainsStringParameter(node))
            {
                throw new PolicyParseException("CASE, COALESCE and NOT over a string policy parameter are not permitted for Oracle (an empty string is NULL).");
            }

            return true;
        });
    }

    // Named markers :name become __param_name, which the AST builder turns into a ParameterReference. Quoted strings and
    // delimited identifiers are skipped.
    private static string SubstituteNamedMarkers(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\'' or '"')
            {
                int start = i++;
                while (i < text.Length)
                {
                    if (text[i] == c)
                    {
                        if (i + 1 < text.Length && text[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }

                    i++;
                }

                sb.Append(text, start, i - start);
                continue;
            }

            if (c == ':' && i + 1 < text.Length && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] == '_') &&
                (i == 0 || !(char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                sb.Append("__param_");
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Resolves columns and tables against the catalog and turns every value into a policy parameter.</summary>
    private sealed class PolicyShaper : SqlAstRewriter
    {
        private readonly PolicyParseContext _context;
        private readonly string _hash8;
        private readonly Dictionary<string, string> _columns;
        private int _nodes;
        private int _literalCounter;
        private int _subqueryDepth;
        private readonly Stack<Dictionary<string, TableCatalogEntry>> _scopes = new();

        public Dictionary<string, PolicyValue> LiteralValues { get; } = new(StringComparer.Ordinal);
        public List<string> DeclaredNames { get; } = new();

        public PolicyShaper(PolicyParseContext context, string hash8)
        {
            _context = context;
            _hash8 = hash8;
            _columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in context.AllowedColumns) _columns[c] = c;
        }

        public override SqlNode Visit(SqlNode node)
        {
            if (++_nodes > MaxNodes)
            {
                throw new PolicyParseException("The policy predicate exceeds the maximum number of nodes.");
            }

            if (node is CurrentDateTimeExpression or TypedLiteralExpression or IntervalLiteralExpression or DateFunctionExpression or TrustedSqlExpression)
            {
                // Typed date literals and clocks would either embed a value in the cached shape or be non-deterministic.
                throw Reject("The policy predicate uses a construct that is not permitted in policies.");
            }

            return base.Visit(node);
        }

        private static PolicyParseException Reject(string reason) => new(reason);

        private PolicyParameterExpression NewLiteralParameter(object value, SqlParameterType type, bool isLikePattern = false)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"__pl_{_hash8}_{_literalCounter++}");
            LiteralValues[name] = new PolicyValue(value, type);
            return new PolicyParameterExpression(name, type, ParameterOrigin.Policy, isLikePattern);
        }

        public override SqlNode VisitLiteralExpression(LiteralExpression node)
        {
            switch (node.Type)
            {
                case LiteralType.Null:
                case LiteralType.Boolean:
                    return node;
                case LiteralType.Integer:
                {
                    long v = Convert.ToInt64(node.Value, CultureInfo.InvariantCulture);
                    return v is >= int.MinValue and <= int.MaxValue
                        ? NewLiteralParameter((int)v, SqlParameterType.Int32)
                        : NewLiteralParameter(v, SqlParameterType.Int64);
                }
                case LiteralType.Decimal:
                    return NewLiteralParameter(
                        decimal.Parse(Convert.ToString(node.Value, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture),
                        SqlParameterType.Decimal);
                case LiteralType.String:
                    return NewLiteralParameter(node.Value!.ToString() ?? string.Empty, SqlParameterType.String);
                default:
                    throw Reject("The policy predicate contains an unsupported literal.");
            }
        }

        public override SqlNode VisitBinaryExpression(BinaryExpression node)
        {
            // The canonical tautology and deny-all stay constants.
            if (node.Operator == BinaryOperator.Equal &&
                node.Left is LiteralExpression { Type: LiteralType.Integer, Value: { } l } &&
                node.Right is LiteralExpression { Type: LiteralType.Integer, Value: { } r } &&
                Convert.ToInt64(l, CultureInfo.InvariantCulture) == 1 && Convert.ToInt64(r, CultureInfo.InvariantCulture) is 0 or 1)
            {
                return node;
            }

            return base.VisitBinaryExpression(node);
        }

        public override SqlNode VisitLikeExpression(LikeExpression node)
        {
            if (node.Escape != null)
            {
                throw Reject("LIKE ... ESCAPE is not permitted in policy predicates.");
            }

            var operand = (Expression)Visit(node.Operand);
            Expression pattern = node.Pattern is LiteralExpression { Type: LiteralType.String, Value: { } p }
                ? NewLiteralParameter(p.ToString() ?? string.Empty, SqlParameterType.String, isLikePattern: true)
                : (Expression)Visit(node.Pattern);
            return node with { Operand = operand, Pattern = pattern };
        }

        public override SqlNode VisitParameterReference(ParameterReference node)
        {
            if (!node.IsSynthetic)
            {
                throw Reject("Positional parameters are not permitted in policy predicates.");
            }

            if (!_context.Parameters.TryGetValue(node.Name, out var declared))
            {
                throw Reject("The policy predicate uses an undeclared parameter.");
            }

            if (!DeclaredNames.Contains(node.Name)) DeclaredNames.Add(node.Name);
            return new PolicyParameterExpression(node.Name, declared.Type, ParameterOrigin.Policy);
        }

        public override SqlNode VisitFunctionCallExpression(FunctionCallExpression node)
        {
            if (node.Window != null) throw Reject("Window functions are not permitted in policy predicates.");
            if (node.Filter != null || node.OrderWithin != null) throw Reject("FILTER and ORDER BY inside functions are not permitted in policy predicates.");

            string name = node.Name.NormalizedName;
            if (NondeterministicOrSessionFunctions.Contains(name) || NondeterministicOrSessionFunctions.Contains(node.Name.SimpleName))
            {
                throw Reject("Non-deterministic and session-dependent functions are not permitted in policy predicates.");
            }

            if (!_context.AllowedFunctions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw Reject("The policy predicate uses a function that is not allow-listed.");
            }

            return base.VisitFunctionCallExpression(node);
        }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            var parts = node.Name.Parts;
            string column = node.Name.SimpleName;

            if (_scopes.Count == 0)
            {
                // Top level: unqualified columns of the target table (autheris_target. is accepted as an explicit qualifier).
                if (parts.Count > 2 || (parts.Count == 2 && !string.Equals(parts[0].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Reject("The policy predicate references a column of another relation.");
                }

                return CanonicalTarget(column);
            }

            // Inside a policy subquery.
            if (parts.Count == 2 && string.Equals(parts[0].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase))
            {
                return new ColumnReference(new SqlQualifiedName(new[]
                {
                    new SqlIdentifier(RowFilterAliases.Target), CanonicalTargetIdentifier(column)
                }));
            }

            var scope = _scopes.Peek();
            if (parts.Count == 2)
            {
                if (!scope.TryGetValue(parts[0].Value.ToLowerInvariant(), out var entry) || !HasColumn(entry, column, out var canonical))
                {
                    throw Reject("The policy subquery references an unknown column.");
                }

                return new ColumnReference(new SqlQualifiedName(new[] { parts[0], new SqlIdentifier(canonical, true) }));
            }

            if (parts.Count == 1)
            {
                string? found = null;
                foreach (var entry in scope.Values.Distinct())
                {
                    if (HasColumn(entry, column, out var canonical))
                    {
                        if (found != null) throw Reject("The policy subquery references an ambiguous column.");
                        found = canonical;
                    }
                }

                if (found is null) throw Reject("The policy subquery references an unknown column.");
                return new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(found, true) }));
            }

            throw Reject("The policy subquery references an unsupported column.");
        }

        private static bool HasColumn(TableCatalogEntry entry, string column, out string canonical)
        {
            foreach (var c in entry.Columns)
            {
                if (string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = c.Name;
                    return true;
                }
            }

            canonical = string.Empty;
            return false;
        }

        private ColumnReference CanonicalTarget(string column) =>
            new(new SqlQualifiedName(new[] { CanonicalTargetIdentifier(column) }));

        private SqlIdentifier CanonicalTargetIdentifier(string column) =>
            _columns.TryGetValue(column, out var canonical)
                ? new SqlIdentifier(canonical, true)
                : throw Reject("The policy predicate references an unknown column.");

        // ---- subqueries ----

        private void RequireSubqueryAllowed()
        {
            if (_context.Catalog is null)
            {
                throw Reject("Subqueries are not permitted in this policy predicate (no catalog).");
            }

            if (_subqueryDepth >= 1)
            {
                throw Reject("Nested policy subqueries are not permitted.");
            }
        }

        public override SqlNode VisitSelectStatement(SelectStatement node)
        {
            RequireSubqueryAllowed();
            if (node.With != null || node.OrderBy != null || node.Pagination != null)
            {
                throw Reject("Policy subqueries may only be a plain SELECT ... FROM ... WHERE.");
            }

            if (node.Body is not QuerySpecification spec || spec.Distinct || spec.GroupBy != null || spec.Having != null || spec.From is null)
            {
                throw Reject("Policy subqueries may only be a plain SELECT ... FROM ... WHERE.");
            }

            // Resolve the FROM clause first so columns can be resolved against its tables.
            _subqueryDepth++;
            try
            {
                var scope = new Dictionary<string, TableCatalogEntry>(StringComparer.Ordinal);
                var from = ResolveFrom(spec.From, scope);
                _scopes.Push(scope);
                try
                {
                    var projections = spec.Projections.Select(p => (SelectItem)Visit(p)).ToList();
                    var where = spec.Where != null ? (Expression)Visit(spec.Where) : null;
                    return node with { Body = spec with { Projections = projections, From = from, Where = where } };
                }
                finally
                {
                    _scopes.Pop();
                }
            }
            finally
            {
                _subqueryDepth--;
            }
        }

        private TableSource ResolveFrom(TableSource source, Dictionary<string, TableCatalogEntry> scope)
        {
            switch (source)
            {
                case NamedTableSource named:
                {
                    var entry = _context.Catalog!.Resolve(named.Name)
                        ?? throw Reject("The policy subquery references a table that is not in the catalog.");
                    string key = (named.Alias?.Value ?? named.Name.SimpleName).ToLowerInvariant();
                    if (!scope.TryAdd(key, entry)) throw Reject("The policy subquery uses a duplicate table alias.");
                    var canonicalName = new SqlQualifiedName(new[]
                    {
                        new SqlIdentifier(entry.Identity.Schema, true), new SqlIdentifier(entry.Identity.Table, true)
                    });
                    return new NamedTableSource(canonicalName, named.Alias);
                }
                case JoinedTableSource join when join.Condition is OnJoinCondition or null && join.Type != JoinType.Natural:
                {
                    var left = ResolveFrom(join.Left, scope);
                    var right = ResolveFrom(join.Right, scope);
                    // The ON condition is shaped later, with the scope complete; shape it here after both sides are in scope.
                    _scopes.Push(scope);
                    try
                    {
                        var condition = join.Condition is OnJoinCondition on ? new OnJoinCondition((Expression)Visit(on.Predicate)) : null;
                        return join with { Left = left, Right = right, Condition = condition };
                    }
                    finally
                    {
                        _scopes.Pop();
                    }
                }
                default:
                    throw Reject("The policy subquery uses an unsupported table source.");
            }
        }

        public override SqlNode VisitExistsExpression(ExistsExpression node)
        {
            RequireSubqueryAllowed();
            return node with { Subquery = (SelectStatement)Visit(node.Subquery) };
        }

        public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node)
        {
            RequireSubqueryAllowed();
            return node with { Operand = (Expression)Visit(node.Operand), Subquery = (SelectStatement)Visit(node.Subquery) };
        }

        public override SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node)
        {
            RequireSubqueryAllowed();
            return node with { Subquery = (SelectStatement)Visit(node.Subquery) };
        }

        public override SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node) =>
            throw Reject("Quantified comparisons are not permitted in policy predicates.");

        public override SqlNode VisitWildcardSelectItem(WildcardSelectItem node) =>
            throw Reject("Wildcard projections are not permitted in policy subqueries.");

        public override SqlNode VisitTableQueryBody(TableQueryBody node) =>
            throw Reject("TABLE references are not permitted in policy predicates.");

        public override SqlNode VisitLateralTableSource(LateralTableSource node) =>
            throw Reject("Lateral sources are not permitted in policy predicates.");

        public override SqlNode VisitSubqueryTableSource(SubqueryTableSource node) =>
            throw Reject("Derived tables are not permitted in policy predicates.");
    }
}
