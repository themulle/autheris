using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Issue #277: the governed compiler's WITH RECURSIVE contract (opt-in gate, scope parity with the analyzer, catalog-name shadowing, the
/// SQL Server MAXRECURSION tail, mask tracking through recursion). Security conditions SEC-277-01, -02, -03, -05, -10, -11 (engine part).
/// </summary>
[Trait("Category", "SqlGate")]
public class RecursiveCteGovernanceTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public static IEnumerable<object[]> Dialects() => new[]
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    }.Select(d => new object[] { d });

    public RecursiveCteGovernanceTests()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private static InMemoryTableCatalog CatalogWithoutOrders() => new(new[]
    {
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")),
            "TenantId", 1)
    }, "dbo");

    private CompileRequest Request(
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        bool allow = true,
        int depth = CompileLimits.DefaultMaxRecursionDepth,
        StatementPermissions statements = StatementPermissions.ReadOnly,
        InMemoryTableCatalog? catalog = null) => new()
    {
        TargetDialect = dialect,
        AllowExperimentalDialect = true,
        AllowRecursiveCte = allow,
        MaxRecursionDepth = depth,
        Statements = statements,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = catalog ?? Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            TablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "dbo.Orders" }
        }
    };

    private CompiledSql Compile(string sql, CompileRequest request) => _engine.Compile(sql.AsMemory(), request, CancellationToken.None);

    private CompiledSql Compile(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer, bool allow = true) =>
        Compile(sql, Request(dialect, allow));

    private static string Counter(string name) =>
        $"WITH RECURSIVE {name} (n) AS (SELECT 1 UNION ALL SELECT c.n + 1 FROM {name} c WHERE c.n < 3) SELECT n FROM {name}";

    // ---- SEC-277-01: scope parity ----

    private static readonly string[] ScopeCorpus =
    [
        "WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r",
        "WITH RECURSIVE R (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM R",
        "WITH RECURSIVE \"r\" (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM \"r\" WHERE n < 3) SELECT n FROM \"r\"",
        "WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM \"R\" WHERE n < 3) SELECT n FROM r",
        "SELECT t.n, e.id FROM (WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r) t " +
            "JOIN entitlements e ON e.id = t.n",
        "WITH RECURSIVE a (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM a WHERE n < 2), " +
            "b (n) AS (SELECT n FROM a UNION ALL SELECT n + 1 FROM b WHERE n < 3) SELECT n FROM b",
        "WITH RECURSIVE a (n) AS (SELECT id FROM orders UNION ALL SELECT n FROM b), b (n) AS (SELECT id FROM entitlements) SELECT n FROM a",
        "SELECT q.n, e.id FROM (WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r) q " +
            "JOIN entitlements e ON e.id = q.n WHERE e.id IN (SELECT id FROM orders)",
        "SELECT a.n FROM (WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r) a, " +
            "(WITH q AS (SELECT id AS n FROM entitlements) SELECT n FROM q) b",
        "WITH RECURSIVE entitlements (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM entitlements WHERE n < 3) SELECT n FROM entitlements UNION ALL SELECT id FROM orders",
        "WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT r.n FROM r JOIN orders o ON o.id = r.n"
    ];

    /// <summary>
    /// SEC-277-01: every physical table the compiler secures (an applied tenant predicate) is in the analyzer's <c>ReferencedTables</c>,
    /// which authorization is derived from. A statement where the two disagree must be denied; the compiler stays the authority.
    /// </summary>
    [Trait("Category", "Security")]
    [Fact]
    public void Analyzer_Compiler_ScopeParity_RecursiveCorpus_NoCompilerTableMissingFromAnalyzer()
    {
        int compiled = 0, secured = 0;
        foreach (string sql in ScopeCorpus)
        {
            var analyzed = _engine.Analyze(sql.AsMemory()).ReferencedTables
                .Select(t => t.TableName.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

            CompiledSql result;
            try
            {
                result = Compile(sql);
            }
            catch (SecurityException)
            {
                continue;   // denied: fail closed is the allowed outcome of a disagreement
            }

            compiled++;
            foreach (var predicate in result.AppliedPredicates)
            {
                string table = predicate.TableIdentity[(predicate.TableIdentity.LastIndexOf('.') + 1)..].ToLowerInvariant();
                Assert.True(analyzed.Contains(table), $"The compiler secured '{table}' but the analyzer did not report it: {sql}");
                secured++;
            }
        }

        Assert.True(compiled >= 6, $"Only {compiled} corpus statements compiled; the parity check would be vacuous.");
        Assert.True(secured >= 6);
    }

    // ---- SEC-277-02: shadowing ----

    /// <summary>
    /// SEC-277-02: a recursive CTE named like an authorized catalog table is rejected in every spelling and position, with a fixed message
    /// that names no table or schema.
    /// </summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_NameShadowsCatalogTable_Variants_AreRejected(TargetSqlDialect dialect)
    {
        string[] statements =
        [
            Counter("orders"),
            Counter("ORDERS"),
            Counter("\"orders\""),
            Counter("\"ORDERS\""),
            Counter("\"Orders\""),
            "SELECT n FROM (" + Counter("orders") + ") t",
            // a later CTE named like an authorized table (forward and mutual references)
            "WITH RECURSIVE a (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM a WHERE n < 3), orders (n) AS (SELECT n FROM a) SELECT n FROM orders",
            "WITH RECURSIVE a (n) AS (SELECT id FROM entitlements UNION ALL SELECT n FROM orders), entitlements (n) AS (SELECT 1) SELECT n FROM a",
            // the name equals a table that is referenced in a sibling scope
            "WITH RECURSIVE entitlements (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM entitlements WHERE n < 3) " +
                "SELECT n FROM entitlements UNION ALL SELECT id FROM orders"
        ];

        foreach (string sql in statements)
        {
            var ex = Assert.Throws<SecurityException>(() => Compile(sql, dialect));
            Assert.Equal("A recursive CTE must not have the name of a catalog table; the reference would be ambiguous.", ex.Message);
        }
    }

    /// <summary>
    /// SEC-277-02: a recursive name that equals a table the request catalog does not hold (the application catalog holds only the tables the
    /// analyzer reported and the caller may read) is a pure CTE, exactly like a name that is no table anywhere: no existence oracle.
    /// </summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_NameEqualsUnreferencedTable_BehavesLikeUnknownName(TargetSqlDialect dialect)
    {
        var known = Compile(Counter("orders"), Request(dialect, catalog: CatalogWithoutOrders()));
        var unknown = Compile(Counter("zzz"), Request(dialect, catalog: CatalogWithoutOrders()));

        Assert.Equal(unknown.Sql, Regex.Replace(known.Sql, "orders", "zzz", RegexOptions.IgnoreCase), ignoreCase: true);   // Oracle upper-cases the user alias
        Assert.Equal(unknown.Parameters.Length, known.Parameters.Length);
        Assert.Empty(known.AppliedPredicates);   // no physical read of any table
        Assert.Empty(unknown.AppliedPredicates);
        Assert.Equal(unknown.HasRecursiveCte, known.HasRecursiveCte);
    }

    // ---- SEC-277-03: gate order, DML, defaults ----

    /// <summary>
    /// SEC-277-03: with recursion disabled the typed "construct not supported" comes first: a name that is (or is not) a catalog table, a
    /// self-reference or a masked body cannot change the answer, so the shadow check and the catalog are never consulted.
    /// </summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_Disabled_RejectedBeforeShadowCheckAndTableLookup(TargetSqlDialect dialect)
    {
        var catalogName = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(Counter("orders"), dialect, allow: false));
        var unknownName = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(Counter("zzz"), dialect, allow: false));
        var masked = Assert.Throws<SqlCompileNotSupportedException>(() => Compile(
            "WITH RECURSIVE r (n, s) AS (SELECT id, email FROM orders UNION ALL SELECT n + 1, s FROM r WHERE n < 3) SELECT max(s) FROM r", dialect, allow: false));

        Assert.Equal(SqlCompileNotSupportedReason.Construct, catalogName.Reason);
        Assert.Equal(catalogName.Message, unknownName.Message);
        Assert.Equal(catalogName.Message, masked.Message);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecursiveCte_InDml_RejectedEvenWhenEnabled(bool allow)
    {
        const StatementPermissions all =
            StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge;
        const string Recursive = "WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r";
        foreach (string sql in new[]
        {
            "INSERT INTO orders (id, status) WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n, 'x' FROM r",
            "UPDATE orders SET status = 'x' WHERE id IN (" + Recursive + ")",
            "DELETE FROM orders WHERE id IN (" + Recursive + ")",
            "MERGE INTO orders t USING (" + Recursive + ") s ON t.id = s.n WHEN MATCHED THEN UPDATE SET status = 'x'"
        })
        {
            var error = Record.Exception(() => Compile(sql, Request(allow: allow, statements: all)));

            // INSERT ... WITH is already refused by the AST builder (a construct error); the other classes reach the compiler gate.
            Assert.True(
                error is SqlCompileNotSupportedException { Reason: SqlCompileNotSupportedReason.Construct } ||
                (sql.StartsWith("INSERT", StringComparison.Ordinal) && error is TrinoSqlEngine.Ast.Builder.AstBuildException),
                $"{sql} -> {error?.GetType().Name}");
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public void CompileRequest_DefaultsAreDisabled()
    {
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = Request().Policy
        };

        Assert.False(request.AllowRecursiveCte);
        Assert.Equal(100, request.MaxRecursionDepth);
        Assert.Throws<SqlCompileNotSupportedException>(() => Compile(Counter("r"), request));

        // The normalized request keeps the gate closed.
        Assert.False(CompileLimits.Normalize(request).AllowRecursiveCte);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void SecurityControls_HaveNoSwitchForTheRecursiveGate()
    {
        Assert.DoesNotContain(Enum.GetNames<SecurityControl>(), name => name.Contains("Recursi", StringComparison.OrdinalIgnoreCase));
    }

    // ---- limits, cache, audit flag ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(int.MaxValue)]
    public void CompileLimits_RecursionDepthOutOfRange_ThrowsConfigurationException(int depth)
    {
        Assert.Throws<SqlCompileConfigurationException>(() => CompileLimits.Normalize(Request(depth: depth)));
        Assert.Throws<SqlCompileConfigurationException>(() => Compile(Counter("r"), Request(depth: depth)));

        // The bound is validated only when recursion is allowed; a disabled request is rejected by the gate, not by the depth.
        CompileLimits.Normalize(Request(allow: false, depth: depth));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void CompileLimits_RecursionDepthInRange_IsAccepted(int depth) => CompileLimits.Normalize(Request(depth: depth));

    [Fact]
    public void CompileCacheKey_DiffersByAllowRecursiveCteAndMaxRecursionDepth()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer);
        string baseKey = CompileCacheKey.Material(Request(allow: false), "SELECT 1", caps);

        Assert.NotEqual(baseKey, CompileCacheKey.Material(Request(allow: true), "SELECT 1", caps));
        Assert.NotEqual(
            CompileCacheKey.Material(Request(allow: true), "SELECT 1", caps),
            CompileCacheKey.Material(Request(allow: true, depth: 7), "SELECT 1", caps));
    }

    [Fact]
    public void CacheHit_KeepsHasRecursiveCte()
    {
        var request = Request();
        var first = Compile(Counter("r"), request);
        var hit = Compile(Counter("r"), request);
        var plain = Compile("SELECT id FROM orders", request);
        var plainHit = Compile("SELECT id FROM orders", request);

        Assert.True(first.HasRecursiveCte);
        Assert.True(hit.HasRecursiveCte);
        Assert.Equal(first.Sql, hit.Sql);
        Assert.False(plain.HasRecursiveCte);
        Assert.False(plainHit.HasRecursiveCte);
    }

    [Fact]
    public void AllowedThenDisabled_SameEngine_NeverServesTheRecursiveTemplate()
    {
        Compile(Counter("r"), Request(allow: true));
        Assert.Throws<SqlCompileNotSupportedException>(() => Compile(Counter("r"), Request(allow: false)));
    }

    // ---- SEC-277-05: MAXRECURSION ----

    [Fact]
    public void SqlServer_RecursiveCompile_EndsWithTheValidatedMaxRecursionTail()
    {
        foreach (int depth in new[] { 1, 100, 250, 10000 })
        {
            var compiled = Compile(Counter("r"), Request(depth: depth));
            Assert.EndsWith($" OPTION (MAXRECURSION {depth})", compiled.Sql, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(compiled.Sql, "OPTION"));
            Assert.DoesNotContain("RECURSIVE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.True(compiled.HasRecursiveCte);
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void OtherDialects_RecursiveCompile_HasNoOptionClause(TargetSqlDialect dialect)
    {
        var compiled = Compile(Counter("r"), Request(dialect, depth: 250));
        Assert.DoesNotContain("OPTION", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MAXRECURSION", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(compiled.HasRecursiveCte);
    }

    [Fact]
    public void SqlServer_NonRecursiveStatement_HasNoOptionClause()
    {
        var compiled = Compile("WITH a AS (SELECT id FROM orders) SELECT id FROM a");
        Assert.DoesNotContain("OPTION", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(compiled.HasRecursiveCte);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void WebSql_UserOptionHint_IsSyntaxError()
    {
        foreach (string sql in new[]
        {
            "SELECT id FROM orders OPTION (MAXRECURSION 0)",
            "WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT n FROM r OPTION (MAXRECURSION 0)",
            "SELECT id FROM orders OPTION (MAXDOP 1)"
        })
        {
            Assert.ThrowsAny<Exception>(() => Compile(sql));
        }

        // OPTION (RECOMPILE) parses as a table alias with a column alias list, never as a hint: any emitted text has it delimited.
        CompiledSql? aliased = null;
        try { aliased = Compile("SELECT id FROM orders OPTION (RECOMPILE)"); } catch (Exception) { /* rejected: fine as well */ }
        if (aliased is not null)
        {
            Assert.DoesNotContain(" OPTION (", aliased.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("MAXRECURSION", aliased.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    private const string TailSql = "WITH [r] ([n]) AS (SELECT [id] FROM [t]) SELECT [n] FROM [r]";

    private static void Check(string sql, TargetSqlDialect dialect, EmittedRange[] ranges, int depth, SqlStatementClass statementClass = SqlStatementClass.Select) =>
        EmittedSqlInvariantChecker.Check(sql, dialect, ImmutableArray<BoundParameter>.Empty, ranges, structuralOnly: false, statementClass, depth);

    private static EmittedRange Tail(string sql, string tail) =>
        new(sql.Length - tail.Length, tail.Length, EmittedRangeKind.RecursionOptionTail);

    /// <summary>SEC-277-05: the checker accepts exactly one final ` OPTION (MAXRECURSION n)` on SQL Server with n equal to the validated depth.</summary>
    [Trait("Category", "Security")]
    [Fact]
    public void SqlServer_MaxRecursionTail_OnlyExactFormAccepted()
    {
        const string Tail100 = " OPTION (MAXRECURSION 100)";
        string good = TailSql + Tail100;
        Check(good, TargetSqlDialect.SqlServer, [Tail(good, Tail100)], 100);   // the exact form passes

        // a second tail
        string twice = good + Tail100;
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(twice, TargetSqlDialect.SqlServer, [new(good.Length - Tail100.Length, Tail100.Length, EmittedRangeKind.RecursionOptionTail), Tail(twice, Tail100)], 100));

        // another n than the validated one, and an n outside the allowed range
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(good, TargetSqlDialect.SqlServer, [Tail(good, Tail100)], 101));
        string zero = TailSql + " OPTION (MAXRECURSION 0)";
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(zero, TargetSqlDialect.SqlServer, [Tail(zero, " OPTION (MAXRECURSION 0)")], 0));
        string huge = TailSql + " OPTION (MAXRECURSION 10001)";
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(huge, TargetSqlDialect.SqlServer, [Tail(huge, " OPTION (MAXRECURSION 10001)")], 10001));

        // another hint, with or without a registered range
        foreach (string hint in new[] { " OPTION (RECOMPILE)", " OPTION (MAXDOP 1)", " OPTION (MAXRECURSION 100, RECOMPILE)", " OPTION (MAXRECURSION  100)", " option (maxrecursion 100)" })
        {
            string other = TailSql + hint;
            Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(other, TargetSqlDialect.SqlServer, [], 100));
            Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(other, TargetSqlDialect.SqlServer, [Tail(other, hint)], 100));
        }

        // an unregistered exact tail is a hint too
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(good, TargetSqlDialect.SqlServer, [], 100));

        // other dialect, other statement class, text after the tail
        string pg = "WITH \"r\" AS (SELECT \"id\" FROM \"t\") SELECT \"n\" FROM \"r\"" + Tail100;
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(pg, TargetSqlDialect.PostgreSql, [Tail(pg, Tail100)], 100));
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(good, TargetSqlDialect.SqlServer, [Tail(good, Tail100)], 100, SqlStatementClass.Delete));
        string trailing = good + " ORDER BY [n]";
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(trailing, TargetSqlDialect.SqlServer, [new(TailSql.Length, Tail100.Length, EmittedRangeKind.RecursionOptionTail)], 100));
    }

    // ---- SEC-277-10: masks through recursion ----

    /// <summary>SEC-277-10: a masked column in either member of a recursive CTE keeps the predicate, aggregate and ORDER BY guards of a masked column.</summary>
    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_MaskedColumnThroughRecursion_PredicateAndAggregateGuardsReject(TargetSqlDialect dialect)
    {
        // anchor literal, masked column in the recursive member
        const string MaskedInRecursiveMember =
            "WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT r.n + 1, o.email FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ";
        // the reverse: masked column in the anchor, literal in the recursive member
        const string MaskedInAnchor =
            "WITH RECURSIVE r (n, s) AS (SELECT id, email FROM orders UNION ALL SELECT n + 1, 'x' FROM r WHERE n < 3) ";
        // the masked value is only carried by the self-reference
        const string MaskedCarried =
            "WITH RECURSIVE r (n, s) AS (SELECT id, email FROM orders UNION ALL SELECT r.n + 1, r.s FROM r WHERE r.n < 3) ";
        // no column list: the output names come from the anchor, the recursive member renames the masked column
        const string MaskedRenamed =
            "WITH RECURSIVE r AS (SELECT 1 AS n, 'x' AS s UNION ALL SELECT r.n + 1, o.email FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ";

        foreach (string head in new[] { MaskedInRecursiveMember, MaskedInAnchor, MaskedCarried, MaskedRenamed })
        {
            Assert.Throws<SecurityException>(() => Compile(head + "SELECT n FROM r WHERE s = 'x'", dialect));
            Assert.Throws<SecurityException>(() => Compile(head + "SELECT n FROM r ORDER BY s", dialect));
            Assert.Throws<MaskedAggregationException>(() => Compile(head + "SELECT max(s) FROM r", dialect));
            Assert.Throws<MaskedAggregationException>(() => Compile(head + "SELECT n, count(s) FROM r GROUP BY n", dialect));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RecursiveCte_WithoutMaskedColumn_StillAcceptsPredicatesAndAggregates(TargetSqlDialect dialect)
    {
        const string Head = "WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT r.n + 1, r.s FROM r WHERE r.n < 3) ";
        Compile(Head + "SELECT n, s FROM r", dialect);
        Compile(Head + "SELECT max(r.n) FROM r", dialect);
        Compile(Head + "SELECT n FROM r ORDER BY n", dialect);
    }
}
