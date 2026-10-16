using System.Collections.Frozen;
using System.Reflection;
using System.Security;
using System.Text;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// PLAN-BUG-G8-01 (SEC-G8-T1 to T6, T9): the projection mask guards (aggregate/GROUP BY and whole-row) of <see cref="AstSecurityVisitor"/>
/// skip their traversal only when no mask can be reachable, and their reusable state must never weaken or leak. The reference
/// implementation below is a test-only copy of the guards as they were before the allocation work (fresh collections per call, the
/// guard always runs); every decision of the real visitor must equal it.
/// </summary>
[Trait("Category", "SqlGate")]
public class MaskGuardSkipSecurityTests
{
    // ---- configuration under test: the caller-supplied set and the typed provider can disagree ----

    private sealed class Lookup(bool providerMasksOrders) : IMaskedColumnLookup
    {
        public bool HasMask(string normalizedTableName, string columnName) =>
            providerMasksOrders && SimpleName(normalizedTableName).Equals("orders", StringComparison.OrdinalIgnoreCase) &&
            columnName.Equals("email", StringComparison.OrdinalIgnoreCase);
    }

    private static string SimpleName(string name) => name[(name.LastIndexOf('.') + 1)..].Trim('"', '[', ']', '`');

    private sealed class CountingColumns
    {
        public int Calls;

        public IReadOnlyList<string>? Get(string name)
        {
            Calls++;
            return SimpleName(name).ToLowerInvariant() switch
            {
                "orders" => new[] { "Id", "TenantId", "Region", "Status", "Amount", "Email", "OrderId" },
                "entitlements" => new[] { "Id", "TenantId", "OrderId" },
                _ => null
            };
        }
    }

    private static AstSecurityVisitor Visitor(bool providerMasksOrders, bool setListsOrders, CountingColumns? columns = null,
        Func<string, IReadOnlyList<string>?>? tableColumns = null)
    {
        columns ??= new CountingColumns();
        var options = new AstSecurityVisitorOptions
        {
            MaskedColumns = new Lookup(providerMasksOrders),
            TableColumns = tableColumns ?? columns.Get,
            TablesWithMaskedColumns = setListsOrders
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        var typed = new TypedPolicyContext(Catalog(), new DictPolicyProvider(), new DictMaskProvider(),
            new TenantBinding("t", "a", SqlParameterType.String), DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));
        return new AstSecurityVisitor(options, typed);
    }

    public static readonly (bool Provider, bool Set)[] Configs = [(false, false), (true, false), (false, true), (true, true)];

    private static string Outcome(Action act)
    {
        try
        {
            act();
            return "ok";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string Actual(string sql, bool provider, bool set) =>
        Outcome(() => Visitor(provider, set).Visit(CompilerTestHelpers.Build(sql)));

    // ---- reference: the guards as they were before the change (always run, fresh collections per call) ----

    private static object? Invoke(AstSecurityVisitor target, string name, params object[] args)
    {
        var method = typeof(AstSecurityVisitor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length &&
                         m.GetParameters().Zip(args).All(p => p.Second == null || p.First.ParameterType.IsInstanceOfType(p.Second)));
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private static void ReferenceAggregateGuard(AstSecurityVisitor v, QuerySpecification node)
    {
        if (node.From == null) return;
        var tables = new List<(string TableName, string? Alias)>();
        Invoke(v, "CollectTableSources", node.From, tables);
        var derived = new List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)>();
        Invoke(v, "CollectDerivedMaskedSources", node.From, derived);
        if (tables.Count == 0 && derived.Count == 0) return;

        if (node.GroupBy != null) Invoke(v, "CheckAggregationScope", tables, derived, node.GroupBy);

        var stack = new Stack<SqlNode>(16);
        foreach (var item in node.Projections) stack.Push(item);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is SelectStatement) continue;
            if (current is FunctionCallExpression fn && (fn.Window != null || IsAggregate(fn.Name.SimpleName)))
            {
                Invoke(v, "CheckAggregationScope", tables, derived, fn);
                continue;
            }

            Invoke(v, "PushChildren", current, stack);
        }
    }

    private static bool IsAggregate(string name) =>
        ((FrozenSet<string>)typeof(AstSecurityVisitor)
            .GetField("AggregateFunctionNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Contains(name);

    private static void ReferenceWholeRowGuard(AstSecurityVisitor v, QuerySpecification node, CountingColumns columns)
    {
        if (node.From == null) return;
        var tables = new List<(string TableName, string? Alias)>();
        Invoke(v, "CollectTableSources", node.From, tables);
        if (tables.Count == 0) return;

        var stack = new Stack<SqlNode>(16);
        foreach (var item in node.Projections) stack.Push(item);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ColumnReference { Name.Parts.Count: 1 } reference)
            {
                string name = reference.Name.SimpleName;
                foreach (var (tbl, alias) in tables)
                {
                    if (!(bool)Invoke(v, "IsWholeRowName", name, tbl, alias!)! || !(bool)Invoke(v, "HasMaskingForTable", tbl)!)
                        continue;
                    var cols = columns.Get(tbl);
                    if (cols != null && cols.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    throw new SecurityException("whole row");
                }

                continue;
            }

            Invoke(v, "PushChildren", current, stack);
        }
    }

    /// <summary>The outcomes of the reference guards over every query specification of the tree (an empty set means accepted).</summary>
    private static HashSet<string> Reference(string sql, bool provider, bool set)
    {
        var columns = new CountingColumns();
        var visitor = Visitor(provider, set, columns);
        var outcomes = new HashSet<string>();
        foreach (var spec in AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build(sql)))
        {
            string aggregate = Outcome(() => ReferenceAggregateGuard(visitor, spec));
            if (aggregate != "ok") outcomes.Add(aggregate);
            string wholeRow = Outcome(() => ReferenceWholeRowGuard(visitor, spec, columns));
            if (wholeRow != "ok") outcomes.Add(wholeRow);
        }

        return outcomes;
    }

    // Masked columns appear only in projections, aggregates, GROUP BY and window clauses: the predicate guard stays out of the comparison.
    public static readonly string[] Corpus =
    [
        "SELECT id, status FROM orders",
        "SELECT sum(amount), status FROM orders GROUP BY status",
        "SELECT sum(email) FROM orders",
        "SELECT id, count(id), max(email) FROM orders",
        "SELECT status, count(*) FROM orders GROUP BY email",
        "SELECT o FROM orders o",
        "SELECT id, to_json(o) FROM orders o",
        "SELECT orders FROM dbo.orders",
        "SELECT o.* FROM orders o",
        "SELECT count(id), sum(amount), max(email) FROM orders",
        "SELECT max(email), sum(amount) FROM orders",
        "SELECT sum(x.email) FROM (SELECT email FROM orders) x",
        "SELECT x.id FROM (SELECT id FROM entitlements) x",
        "SELECT count(e.id) FROM entitlements e JOIN orders o ON e.orderid = o.id",
        "SELECT sum(o.amount), max(e.id) FROM orders o JOIN entitlements e ON o.id = e.orderid GROUP BY o.region",
        "SELECT max(o.email) FROM orders o JOIN entitlements e ON o.id = e.orderid",
        "SELECT count(e.id), max(o.email) FROM entitlements e JOIN orders o USING (id)",
        "SELECT id FROM orders UNION SELECT id FROM entitlements",
        "SELECT id FROM entitlements UNION SELECT max(email) FROM orders",
        "SELECT id FROM entitlements WHERE orderid IN (SELECT sum(amount) FROM orders)",
        "SELECT id, (SELECT max(o.email) FROM orders o) FROM entitlements",
        "SELECT id, (SELECT max(orders.email) FROM entitlements) FROM orders",
        "SELECT sum(id) OVER (PARTITION BY email) FROM orders",
        "SELECT sum(id) OVER (ORDER BY email) FROM orders",
        "SELECT region, sum(amount) FROM orders GROUP BY ROLLUP (region, email)",
        "SELECT array_agg(id ORDER BY email) FROM orders",
        "SELECT max(amount) FILTER (WHERE email = 'x') FROM orders",
        "SELECT count(id) FROM entitlements",
        // CTE, LATERAL and set operations (the reference runs on a fresh visitor without CTE scope: the CTE cases are compared
        // through the real visitor's outcome against the expectation of the theory below instead, see CteAndLateralCorpus)
        "SELECT id FROM entitlements INTERSECT SELECT max(email) FROM orders",
        "SELECT id FROM entitlements EXCEPT SELECT max(email) FROM orders",
        "SELECT id FROM entitlements EXCEPT SELECT id FROM orders",
        "SELECT sum(l.email) FROM entitlements e, LATERAL (SELECT email FROM orders) l",
        "SELECT count(l.id) FROM entitlements e, LATERAL (SELECT id FROM orders) l",
        "SELECT sum(v.x) FROM (VALUES (1), (2)) AS v(x)"
    ];

    /// <summary>CTE cases: the guard depends on the CTE scope of the visit, which the reference (a fresh visitor) does not have.</summary>
    public static readonly (string Sql, string MaskedOutcome)[] CteAndLateralCorpus =
    [
        ("WITH c AS (SELECT email FROM orders) SELECT sum(email) FROM c", "MaskedAggregationException"),
        ("WITH c(e) AS (SELECT email FROM orders) SELECT sum(e) FROM c", "MaskedAggregationException"),
        ("WITH c AS (SELECT email AS m FROM orders) SELECT max(m) FROM c", "MaskedAggregationException"),
        ("WITH c AS (SELECT email FROM orders), d AS (SELECT email FROM c) SELECT sum(email) FROM d", "MaskedAggregationException"),
        ("WITH c AS (SELECT email FROM orders) SELECT max(x.email) FROM c x", "MaskedAggregationException"),
        ("WITH c AS (SELECT email FROM orders) SELECT id FROM entitlements UNION SELECT max(email) FROM c", "MaskedAggregationException"),
        ("WITH RECURSIVE c(e) AS (SELECT email FROM orders UNION ALL SELECT e FROM c) SELECT sum(e) FROM c", "MaskedAggregationException"),
        ("SELECT sum(l.email) FROM entitlements e, LATERAL (SELECT email FROM orders) l", "MaskedAggregationException"),
        ("SELECT sum(l.e) FROM entitlements e, LATERAL (SELECT email FROM orders) l(e)", "MaskedAggregationException"),
        ("SELECT id FROM entitlements INTERSECT SELECT max(email) FROM orders", "MaskedAggregationException"),
        ("SELECT id FROM entitlements EXCEPT SELECT max(email) FROM orders", "MaskedAggregationException")
    ];

    [Fact]
    public void CteAndLateral_MaskedVariantRejected_UnmaskedVariantAccepted()
    {
        var failures = new List<string>();
        foreach (var (sql, masked) in CteAndLateralCorpus)
        {
            string a = Actual(sql, provider: true, set: true);
            string b = Actual(sql, provider: true, set: false);
            string c = Actual(sql, provider: false, set: false);
            if (a != masked) failures.Add($"masked+set: {sql} -> {a}");
            if (b != masked) failures.Add($"masked, empty set: {sql} -> {b}");
            if (c != "ok") failures.Add($"unmasked: {sql} -> {c}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Differential_RealVisitorEqualsTheReferenceGuards_ForEveryQueryAndConfiguration()
    {
        var mismatches = new List<string>();
        int rejected = 0;
        foreach (var (provider, set) in Configs)
        {
            foreach (string sql in Corpus)
            {
                var reference = Reference(sql, provider, set);
                string actual = Actual(sql, provider, set);
                bool equal = reference.Count == 0 ? actual == "ok" : reference.Contains(actual);
                if (reference.Count > 0) rejected++;
                if (!equal)
                    mismatches.Add($"provider={provider} set={set}: {sql} -> actual {actual}, reference [{string.Join(",", reference)}]");
            }
        }

        Assert.Empty(mismatches);
        Assert.True(rejected >= 20, "the corpus must exercise the rejecting branches of the guards");
    }

    // ---- SEC-G8-T2: skip equivalence, masked and unmasked variant ----

    [Theory]
    [InlineData("SELECT sum(email) FROM orders", "MaskedAggregationException")]
    [InlineData("SELECT status, count(*) FROM orders GROUP BY email", "MaskedAggregationException")]
    [InlineData("SELECT sum(o.email) FROM orders o", "MaskedAggregationException")]
    [InlineData("SELECT max(email) FROM dbo.orders", "MaskedAggregationException")]
    [InlineData("SELECT sum(x.email) FROM (SELECT email FROM orders) x", "MaskedAggregationException")]
    [InlineData("SELECT id FROM entitlements UNION SELECT max(email) FROM orders", "MaskedAggregationException")]
    [InlineData("SELECT id, (SELECT max(o.email) FROM orders o) FROM entitlements", "MaskedAggregationException")]
    [InlineData("SELECT sum(id) OVER (PARTITION BY email) FROM orders", "MaskedAggregationException")]
    [InlineData("SELECT id FROM entitlements INTERSECT SELECT max(email) FROM orders", "MaskedAggregationException")]
    [InlineData("SELECT id FROM entitlements EXCEPT SELECT max(email) FROM orders", "MaskedAggregationException")]
    [InlineData("SELECT sum(l.email) FROM entitlements e, LATERAL (SELECT email FROM orders) l", "MaskedAggregationException")]
    [InlineData("SELECT o FROM orders o", "SecurityException")]
    [InlineData("SELECT id, to_json(o) FROM orders o", "SecurityException")]
    public void MaskedVariant_IsRejected_UnmaskedVariantIsAccepted(string sql, string expected)
    {
        Assert.Equal(expected, Actual(sql, provider: true, set: true));
        Assert.Equal("ok", Actual(sql, provider: false, set: false));
    }

    [Fact]
    public void CorrelatedMaskedOuterColumn_InAnUnmaskedInnerSpecification_IsStillRejected()
    {
        // the inner FROM (entitlements) is unmasked; the masked outer table is reachable only through the enclosing specification
        const string sql = "SELECT id, (SELECT max(orders.email) FROM entitlements) FROM orders";

        Assert.Equal("MaskedAggregationException", Actual(sql, provider: true, set: false));
        Assert.Equal("MaskedAggregationException", Actual(sql, provider: true, set: true));
    }

    // ---- SEC-G8-T3: provider and caller-supplied set disagree (fail closed, as before the change) ----

    [Theory]
    [InlineData("SELECT sum(email) FROM orders")]
    [InlineData("SELECT o FROM orders o")]
    [InlineData("SELECT max(o.email) FROM orders o")]
    [InlineData("SELECT sum(x.email) FROM (SELECT email FROM orders) x")]
    public void ProviderMasksButSetIsEmpty_GuardStillRuns(string sql)
    {
        Assert.NotEqual("ok", Actual(sql, provider: true, set: false));
        Assert.Equal(Reference(sql, true, false).Single(), Actual(sql, provider: true, set: false));
    }

    [Theory]
    [InlineData("SELECT o FROM orders o")]
    [InlineData("SELECT to_json(o) FROM orders o")]
    public void SetListsTableButProviderIsEmpty_GuardStillRuns(string sql)
    {
        Assert.Equal("SecurityException", Actual(sql, provider: false, set: true));
    }

    [Fact]
    public void ProviderMaskReportedOnlyThroughAnAlias_StillRunsTheGuard()
    {
        // the guards probe the alias as a column qualifier: an alias that is itself a masked catalog name keeps the guard active
        var visitor = Visitor(providerMasksOrders: true, setListsOrders: false);
        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build("SELECT sum(email) FROM entitlements orders")).Single();

        bool ran = Prepare(visitor, spec);

        Assert.True(ran);
    }

    [Fact]
    public void FailingTableColumnsCallback_RunsTheGuardInsteadOfSkipping()
    {
        var visitor = Visitor(false, false, tableColumns: _ => throw new InvalidOperationException("catalog down"));
        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build("SELECT sum(amount) FROM orders")).Single();

        Assert.Throws<InvalidOperationException>(() => Prepare(visitor, spec));   // the guard ran and met the same failing lookup
    }

    private static bool Prepare(AstSecurityVisitor visitor, QuerySpecification spec)
    {
        Invoke(visitor, "PrepareSpecSources", spec);
        try { return (bool)Invoke(visitor, "EnsureProjectionMaskGuards", spec)!; }
        catch (SecurityException) { return true; }
    }

    [Fact]
    public void TableColumnsUnknown_WithProviderMask_GuardStillRuns()
    {
        var visitor = Visitor(true, false, tableColumns: _ => null);

        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build("SELECT sum(email) FROM orders")).Single();
        Invoke(visitor, "PrepareSpecSources", spec);

        Assert.Throws<MaskedAggregationException>(() => Invoke(visitor, "EnsureProjectionMaskGuards", spec));
        Assert.Equal("MaskedAggregationException",
            Outcome(() => Visitor(true, false, tableColumns: _ => null).Visit(CompilerTestHelpers.Build("SELECT sum(email) FROM orders"))));
    }

    [Fact]
    public void TableColumnsOmittingTheMaskedColumn_IsRejectedByTheGovernedCompile()
    {
        // The visitor trusts the catalog column list (a column the catalog does not list cannot be bound). A list that omits a masked
        // column is therefore a catalog inconsistency; the full compile must still reject the reference.
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(Orders, System.Collections.Immutable.ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Amount", "decimal(18,2)")),
                "TenantId", 1)
        }, "dbo");
        var masks = new DictMaskProvider();
        masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _rowFilters.NoPolicy.Add(Orders);
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = masks,
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };

        Assert.NotEqual("ok", Compile(NewEngine(), "SELECT sum(email) FROM orders", request));
    }

    [Fact]
    public void UnboundQualifierNamingAMaskedCatalogTable_IsRejectedByTheGovernedCompile()
    {
        // other.email: "orders" is a masked catalog table that is not in the FROM clause (an unbound reference)
        var request = EngineRequest(masked: true, listSet: false);

        Assert.NotEqual("ok", Compile(NewEngine(), "SELECT sum(orders.email) FROM entitlements o", request));
        Assert.NotEqual("ok", Compile(NewEngine(), "SELECT sum(other.email) FROM orders o", request));
    }

    // ---- SEC-G8-T4: stack ownership of the projection traversal ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void MaskedAggregate_AtAnyProjectionPosition_IsRejected(int count)
    {
        foreach (int position in new[] { 0, count / 2, count - 1 }.Distinct())
        {
            var items = Enumerable.Range(0, count).Select(i => i == position ? "max(email)" : "sum(amount)");
            string sql = $"SELECT {string.Join(", ", items)} FROM orders";

            Assert.Equal("MaskedAggregationException", Actual(sql, provider: true, set: true));
            Assert.Equal("MaskedAggregationException", Actual(sql, provider: true, set: false));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void WholeRowReference_AtAnyProjectionPosition_IsRejected(int count)
    {
        foreach (int position in new[] { 0, count / 2, count - 1 }.Distinct())
        {
            var items = Enumerable.Range(0, count).Select(i => i == position ? "o" : "id");
            string sql = $"SELECT {string.Join(", ", items)} FROM orders o";

            Assert.Equal("SecurityException", Actual(sql, provider: true, set: true));
        }
    }

    [Fact]
    public void AggregateThenMaskedAggregate_FromAnUnmaskedFirstItem_IsRejected()
    {
        // the first aggregate (unmasked) runs the nested column check, which clears its own stack: the later items must still be seen
        Assert.Equal("MaskedAggregationException", Actual("SELECT count(id), sum(amount), max(email) FROM orders", true, true));
        Assert.Equal("MaskedAggregationException", Actual("SELECT sum(amount), max(email), count(id) FROM orders", true, true));
    }

    // Tripwire only: it pins the private field names so that a rename or a merge of the two stacks fails here and gets a review. The
    // behavioural proof of SEC-G8-02 is the position tests above.
    [Fact]
    public void ProjectionGuardStack_IsNotTheStackTheNestedColumnCheckClears()
    {
        var fields = typeof(AstSecurityVisitor).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(Stack<SqlNode>)).Select(f => f.Name).ToList();

        Assert.Contains("_maskGuardStack", fields);
        Assert.Contains("_projectionGuardStack", fields);
    }

    // ---- SEC-G8-T5: no state leak between compiles ----

    private readonly DictPolicyProvider _rowFilters = new();

    private FastSqlEngine NewEngine() => new() { InlineParseMaxLength = 100_000 };

    private CompileRequest EngineRequest(bool masked, bool listSet)
    {
        var masks = new DictMaskProvider();
        if (masked)
            masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = masks,
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                TablesWithMaskedColumns = listSet ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "dbo.Orders" } : FrozenSet<string>.Empty
            }
        };
    }

    private static string Compile(FastSqlEngine engine, string sql, CompileRequest request) =>
        Outcome(() => engine.Compile(sql.AsMemory(), request, CancellationToken.None));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternatingMaskedAndCleanQueries_OnOneEngine_NeverLeakState(bool listSet)
    {
        var engine = NewEngine();
        var masked = EngineRequest(masked: true, listSet);
        var clean = EngineRequest(masked: false, listSet: false);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("ok", Compile(engine, "SELECT id, status FROM orders", masked));
            Assert.Equal("MaskedAggregationException", Compile(engine, "SELECT sum(email) FROM orders", masked));   // rejecting, then accepted
            Assert.Equal("ok", Compile(engine, "SELECT sum(amount) FROM orders", masked));
            Assert.Equal("SecurityException", Compile(engine, "SELECT o FROM orders o", masked));
            Assert.Equal("ok", Compile(engine, "SELECT sum(email), max(id) FROM orders", clean));   // unmasked after masked: accepted
            Assert.Equal("ok", Compile(engine, "SELECT id FROM entitlements", masked));
        }
    }

    [Fact]
    public void OneVisitorPerCompile_SameOptionsReused_DecisionsDependOnlyOnTheQuery()
    {
        var columns = new CountingColumns();
        var options = new AstSecurityVisitorOptions
        {
            MaskedColumns = new Lookup(true),
            TableColumns = columns.Get
        };
        var typed = new TypedPolicyContext(Catalog(), new DictPolicyProvider(), new DictMaskProvider(),
            new TenantBinding("t", "a", SqlParameterType.String), DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("MaskedAggregationException",
                Outcome(() => new AstSecurityVisitor(options, typed).Visit(CompilerTestHelpers.Build("SELECT sum(email) FROM orders"))));
            Assert.Equal("ok",
                Outcome(() => new AstSecurityVisitor(options, typed).Visit(CompilerTestHelpers.Build("SELECT sum(id) FROM entitlements"))));
        }
    }

    [Fact]
    public void MaskingLookupPerTable_IsCachedPerVisitor()
    {
        var columns = new CountingColumns();
        var visitor = Visitor(false, false, columns);
        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build("SELECT sum(amount) FROM orders o")).Single();

        Prepare(visitor, spec);
        int afterFirst = columns.Calls;
        Prepare(visitor, spec);
        Prepare(visitor, spec);

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, columns.Calls);   // the TableColumns callback allocates: the answer is asked once per table name
    }

    // ---- SEC-G8-T6: concurrency and structure ----

    [Fact]
    public async Task ConcurrentCompiles_OnOneEngine_MatchTheSequentialResults()
    {
        var engine = NewEngine();
        var masked = EngineRequest(masked: true, listSet: false);
        var queries = new[]
        {
            "SELECT id, status FROM orders", "SELECT sum(email) FROM orders", "SELECT o FROM orders o", "SELECT sum(amount) FROM orders",
            "SELECT count(id), max(email) FROM orders", "SELECT id FROM entitlements", "SELECT status, count(*) FROM orders GROUP BY email"
        };
        var expected = queries.ToDictionary(q => q, q => Compile(engine, q, masked));
        Assert.Equal("MaskedAggregationException", expected["SELECT sum(email) FROM orders"]);
        Assert.Equal("ok", expected["SELECT sum(amount) FROM orders"]);

        var tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            var failures = new List<string>();
            for (int i = 0; i < 150; i++)
            {
                // A distinct select-list alias per iteration makes the text unique, so the plan cache never answers: every compile runs the
                // security visitor concurrently (a cache hit would bypass it and prove nothing).
                string baseSql = queries[random.Next(queries.Length)];
                string sql = baseSql.Insert("SELECT ".Length, $"{i} AS k{worker}, ");
                string actual = Compile(engine, sql, masked);
                if (actual != expected[baseSql]) failures.Add($"{baseSql}: {actual} instead of {expected[baseSql]}");
            }

            return failures;
        })).ToArray();

        var all = (await Task.WhenAll(tasks)).SelectMany(f => f).ToList();
        Assert.Empty(all);
    }

    [Fact]
    public void Visitor_HasNoMutableStaticState_AndIsNotHeldByTheEngine()
    {
        var mutableStatics = typeof(AstSecurityVisitor).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsLiteral && (!f.IsInitOnly || IsMutableCollection(f.FieldType)))
            .Select(f => f.Name).ToList();
        Assert.Empty(mutableStatics);

        foreach (var holder in new[] { typeof(FastSqlEngine), typeof(GovernedSqlCompiler) })
        {
            var held = holder.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(AstSecurityVisitor) || f.FieldType == typeof(Stack<SqlNode>)).ToList();
            Assert.Empty(held);
        }
    }

    private static bool IsMutableCollection(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() is var d &&
        (d == typeof(List<>) || d == typeof(Stack<>) || d == typeof(Dictionary<,>) || d == typeof(HashSet<>) || d == typeof(Queue<>));

    // ---- SEC-G8-T9: the unmasked path is allocation-lean, and the guard demonstrably still runs ----

    private static string WideSelect(int projections, bool withMaskedAggregate)
    {
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < projections; i++) sb.Append(i == 0 ? "" : ", ").Append("sum(amount) + ").Append(i);
        if (withMaskedAggregate) sb.Append(", max(email)");
        return sb.Append(" FROM orders").ToString();
    }

    [Fact]
    public void UnmaskedSpecification_SkipsTheProjectionGuards_WithoutPerSpecificationAllocations()
    {
        var visitor = Visitor(providerMasksOrders: false, setListsOrders: false);
        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build(WideSelect(400, withMaskedAggregate: false))).Single();
        var prepare = (Action<QuerySpecification>)Delegate.CreateDelegate(typeof(Action<QuerySpecification>), visitor,
            typeof(AstSecurityVisitor).GetMethod("PrepareSpecSources", BindingFlags.NonPublic | BindingFlags.Instance)!);
        var guard = (Func<QuerySpecification, bool>)Delegate.CreateDelegate(typeof(Func<QuerySpecification, bool>), visitor,
            typeof(AstSecurityVisitor).GetMethod("EnsureProjectionMaskGuards", BindingFlags.NonPublic | BindingFlags.Instance)!);

        for (int i = 0; i < 50; i++)   // warm-up: JIT, the per-table lookup cache
        {
            prepare(spec);
            guard(spec);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ran = false;
        for (int i = 0; i < 100; i++)
        {
            prepare(spec);
            ran |= guard(spec);
        }

        long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        Assert.False(ran);                        // nothing masked: the traversal was skipped
        Assert.True(perCall < 1024, $"unmasked guard path allocated {perCall} bytes per specification");   // a 400-item stack alone was ~4 KB
    }

    [Fact]
    public void MaskedSpecification_RunsTheProjectionGuards_SoTheAllocationTestCannotPassBySkipping()
    {
        var visitor = Visitor(providerMasksOrders: true, setListsOrders: false);
        var spec = AstReflection.Collect<QuerySpecification>(CompilerTestHelpers.Build(WideSelect(400, withMaskedAggregate: true))).Single();

        Invoke(visitor, "PrepareSpecSources", spec);
        var ex = Assert.Throws<MaskedAggregationException>(() => Invoke(visitor, "EnsureProjectionMaskGuards", spec));

        Assert.NotNull(ex);
        Assert.Equal("MaskedAggregationException", Actual(WideSelect(400, withMaskedAggregate: true), provider: true, set: false));
    }
}
