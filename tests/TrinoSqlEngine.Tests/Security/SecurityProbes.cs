using System.Collections.Immutable;
using System.Data;
using System.Security;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;
using static TrinoSqlEngine.Tests.Security.ProbeAsserts;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// One executable security assertion of the gate (WP-X0v, G7). A probe passes on the real pipeline. The mutant suite runs every
/// probe again with one control switched off and requires that at least one probe fails: that proves the control is guarded.
/// A probe is deterministic, has its own engine and plan cache, and needs no network or container.
/// </summary>
internal sealed record SecurityProbe(string Name, string Control, Action Run)
{
    public override string ToString() => Name;
}

internal static class SecurityProbes
{
    private static readonly TargetSqlDialect[] Production =
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks
    };

    private static readonly TargetSqlDialect[] MergeDialects = Production;

    public static IReadOnlyList<SecurityProbe> All { get; } = Build();

    private static List<SecurityProbe> Build()
    {
        var all = new List<SecurityProbe>();

        void Add(string name, string control, Action run) => all.Add(new SecurityProbe(name, control, run));
        void PerDialect(string name, string control, Action<TargetSqlDialect> run, IEnumerable<TargetSqlDialect>? dialects = null)
        {
            foreach (var d in dialects ?? Production) Add($"{name}[{d}]", control, () => run(d));
        }

        // ---- tenant predicate at every scope (INV-3, plan 4.3) ------------------------------------------------------------
        // Each scenario states how many tenant predicates the statement must carry. The count is read from the emitted text, so
        // it holds even when the coverage verifier is switched off.
        (string Name, string Sql, int Predicates, Action<ProbeEnv>? Prepare)[] reads =
        {
            ("Root", "SELECT id FROM orders", 1, null),
            ("Cte", "WITH c AS (SELECT id FROM orders) SELECT id FROM c", 1, null),
            ("SetOperationBranch", "SELECT id FROM orders UNION ALL SELECT id FROM entitlements", 2, null),
            ("ScalarSubquery", "SELECT id, (SELECT max(id) FROM entitlements) FROM orders", 2, null),
            ("InSubquery", "SELECT id FROM orders WHERE id IN (SELECT orderid FROM entitlements)", 2, null),
            ("ExistsSubquery", "SELECT id FROM orders WHERE EXISTS (SELECT 1 FROM entitlements WHERE entitlements.orderid = orders.id)", 2, null),
            ("PolicySubquery", "SELECT id FROM orders", 2, env => env.PolicySubqueryPolicy()),
            ("ConsentFilterWithinTenant", "SELECT id FROM orders", 1, env => env.RegionPolicy()),
            ("CteNamedLikeAQuotedPhysicalName", "WITH \"ORDERS\" AS (SELECT 1 AS x) SELECT x FROM orders", 1, null)
        };
        foreach (var (scenario, sql, predicates, prepare) in reads)
        {
            PerDialect($"TenantPredicate_{scenario}", "tenant-predicate", d =>
            {
                var env = new ProbeEnv(d);
                prepare?.Invoke(env);
                TenantPredicates(env.Compile(sql), predicates);
            });
        }

        PerDialect("TenantPredicate_UpdateAndDeleteTarget", "tenant-predicate", d =>
        {
            var env = new ProbeEnv(d);
            TenantPredicates(env.Compile("UPDATE orders SET status = 'x' WHERE id = 1"), 1);
            TenantPredicates(env.Compile("DELETE FROM orders WHERE id = 1"), 1);
        });

        PerDialect("TenantPredicate_MergeTargetAndSource", "tenant-predicate", d =>
        {
            var env = new ProbeEnv(d);
            var c = env.Compile("MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'");
            Assert.True(TenantMarkerMentions(c) >= 2 * MentionsPerTenantPredicate(d), "the MERGE target (ON) and the source must both carry the tenant predicate");
        }, MergeDialects);

        PerDialect("ConsentFilter_AppliesInsideTheTenant", "tenant-predicate", d =>
        {
            var env = new ProbeEnv(d);
            env.RegionPolicy("EU");
            var c = env.Compile("SELECT id FROM orders");
            Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        });

        PerDialect("MaskedColumn_IsNeverProjectedInClear", "mask", d =>
        {
            var env = new ProbeEnv(d);
            env.MaskEmail();
            var c = env.Compile("SELECT * FROM orders");
            Assert.DoesNotMatch(@"[\[""`]Email[\]""`]\s+AS\s+[\[""`]Email", c.Sql);
        });

        // ---- verifier, text checker, literals ----------------------------------------------------------------------------
        PerDialect("Verifier_RejectsAnUnsecuredStatement", "verifier", d =>
        {
            var env = new ProbeEnv(d);
            using var _ = MutantContext.Overlay(injector: _ => Unsecured());
            Assert.Throws<SecurityCoverageException>(() => env.Compile("SELECT id FROM orders"));
            Assert.Equal(0, env.Engine.CompileCache.Stats.Entries);
        });

        PerDialect("Verifier_RejectsAnUnknownMergeClause", "merge-clause-set", d =>
        {
            var env = new ProbeEnv(d);
            using var _ = MutantContext.Overlay(injector: Rewrite(new AddUnknownMergeClauseRewriter()));
            Assert.Throws<SecurityCoverageException>(() =>
                env.Compile("MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'"));
            Assert.Equal(0, env.Engine.CompileCache.Stats.Entries);
        }, MergeDialects);

        PerDialect("TextChecker_RejectsASmuggledLiteral", "text-checker", d =>
        {
            var env = new ProbeEnv(d);
            using var _ = MutantContext.Overlay(emitter: sql => sql.Replace("SELECT", "SELECT 'smuggled',", StringComparison.Ordinal));
            Assert.Throws<EmittedSqlInvariantViolationException>(() => env.Compile("SELECT id FROM orders"));
            Assert.Equal(0, env.Engine.CompileCache.Stats.Entries);
        });

        PerDialect("TextChecker_RejectsAStatementSeparatorAndAnUnboundNumber", "text-checker", d =>
        {
            var env = new ProbeEnv(d);
            using (MutantContext.Overlay(emitter: sql => sql + "; SELECT 1"))
            {
                Assert.Throws<EmittedSqlInvariantViolationException>(() => env.Compile("SELECT id FROM orders"));
            }

            using (MutantContext.Overlay(emitter: sql => sql + " WHERE 1 = 1 OR 42 = 42"))
            {
                Assert.Throws<EmittedSqlInvariantViolationException>(() => env.Compile("SELECT id FROM orders"));
            }
        });

        PerDialect("Literal_StringIsBound_NeverEmitted", "bound-literal", d =>
        {
            var env = new ProbeEnv(d);
            var c = env.Compile("SELECT id FROM orders WHERE status = 'open'");
            Assert.DoesNotContain("'open'", c.Sql);
            Assert.DoesNotContain("open", c.Sql);
            Assert.Contains(c.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "open"));
        });

        // ---- tenant comparison is byte-exact (SEC-ADG-04, INV-15) ---------------------------------------------------------
        PerDialect("TenantComparison_IsByteExact", "tenant-compare", d =>
        {
            var caps = DialectCapabilityTable.Default.Get(d);
            var predicate = TenantPredicateFactory.Build(caps, "TenantId", "t", SqlParameterType.String);
            var conjuncts = Conjuncts(predicate);
            Assert.Equal(d switch
            {
                TargetSqlDialect.SqlServer => 3,
                TargetSqlDialect.Databricks => 1,
                _ => 2
            }, conjuncts.Count);

            // a plain equality is never the whole comparison: at least one conjunct compares transformed (binary) operands
            Assert.Contains(conjuncts, c => c is BinaryExpression { Operator: BinaryOperator.Equal, Left: not ColumnReference });
            if (d == TargetSqlDialect.SqlServer)
            {
                Assert.Contains(conjuncts, c => c is BinaryExpression { Left: FunctionCallExpression f } && f.Name.SimpleName.Equals("DATALENGTH", StringComparison.OrdinalIgnoreCase));
            }
        });

        // ---- DML guards ---------------------------------------------------------------------------------------------------
        PerDialect("Dml_TenantColumnAssignment_IsRejected", "dml-tenant-column", d =>
        {
            var env = new ProbeEnv(d);
            Assert.ThrowsAny<SecurityException>(() => env.Compile("UPDATE orders SET tenantid = 'other' WHERE id = 1"));
            Assert.ThrowsAny<SecurityException>(() => env.Compile("UPDATE orders SET TENANTID = 'acme' WHERE id = 1"));
        });

        PerDialect("Dml_PolicyColumnAssignment_IsRejected", "dml-policy-column", d =>
        {
            var env = new ProbeEnv(d);
            env.RegionPolicy();
            Assert.ThrowsAny<SecurityException>(() => env.Compile("UPDATE orders SET region = 'US' WHERE id = 7"));
        });

        PerDialect("Dml_UnfilteredOrTriviallyTrue_IsRejected", "dml-unfiltered", d =>
        {
            var env = new ProbeEnv(d);
            Assert.Throws<UnfilteredDmlException>(() => env.Compile("DELETE FROM orders"));
            Assert.Throws<UnfilteredDmlException>(() => env.Compile("UPDATE orders SET status = 'x' WHERE 1 = 1"));
            Assert.Throws<UnfilteredDmlException>(() => env.Compile("MERGE INTO orders t USING entitlements s ON 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'"));
        }, MergeDialects);

        // ---- binder and checked execution (CR-ADG-43, CR-ADG-45, SEC-ADG-03) --------------------------------------------------
        Add("Binder_RefusesAStatementThatNeedsTheCheckedExecutor", "binder-refusal", () =>
        {
            using var command = new SqliteCommand();
            var compiled = Checked("INSERT INTO t (a) SELECT 1", expected: 1);
            Assert.Throws<CheckedExecutionRequiredException>(() => new SqlServerCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>()));
        });

        Add("Binder_RefusesTheReservedCheckAlias_EvenWhenTheFlagIsLost", "binder-alias", () =>
        {
            using var command = new SqliteCommand();
            var flagLost = Checked("INSERT INTO t (a) SELECT 1 FROM (SELECT 1 AS a) autheris_ins", expected: null);
            Assert.False(flagLost.RequiresRowCountCheck);
            Assert.Throws<CheckedExecutionRequiredException>(() => new SqlServerCompiledSqlBinder().Bind(command, flagLost, new Dictionary<string, object?>()));
        });

        Add("Binder_Oracle_EnablesAndVerifiesBindByName", "oracle-bind-by-name", () =>
        {
            var compiled = Checked("SELECT 1 FROM dual", expected: null, TargetSqlDialect.Oracle);
            using var withProperty = new FakeOracleCommand();
            new OracleCompiledSqlBinder().Bind(withProperty, compiled, new Dictionary<string, object?>());
            Assert.True(withProperty.BindByName, "ODP.NET binds by position unless BindByName is true");

            using var withoutProperty = new SqliteCommand();
            Assert.Throws<SecurityException>(() => new OracleCompiledSqlBinder().Bind(withoutProperty, compiled, new Dictionary<string, object?>()));
        });

        Add("CheckedExecutor_RollsBackAndThrows_WhenTheAffectedCountDiffers", "checked-row-count", () =>
        {
            using var connection = OpenTable();
            var compiled = Checked("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", expected: 1, TargetSqlDialect.DuckDb);
            var executor = new CheckedDmlExecutor(new DuckDbCompiledSqlBinder());
            Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => executor.ExecuteAsync(connection, compiled, new Dictionary<string, object?>())).GetAwaiter().GetResult();
            Assert.Equal(0, Count(connection));
        });

        Add("CheckedExecutor_NeverCommitsBeforeTheCountIsChecked", "checked-commit-order", () =>
        {
            using var connection = OpenTable();
            var compiled = Checked("INSERT INTO t (a) SELECT 1 WHERE 1 = 2", expected: 1, TargetSqlDialect.DuckDb);   // writes 0 rows, 1 expected
            var executor = new CheckedDmlExecutor(new DuckDbCompiledSqlBinder());
            Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => executor.ExecuteAsync(connection, compiled, new Dictionary<string, object?>())).GetAwaiter().GetResult();
            Assert.Equal(0, Count(connection));

            var matching = Checked("INSERT INTO t (a) SELECT 7", expected: 1, TargetSqlDialect.DuckDb);
            Assert.Equal(1, executor.ExecuteAsync(connection, matching, new Dictionary<string, object?>()).GetAwaiter().GetResult());
            Assert.Equal(1, Count(connection));
        });

        // ---- plan cache poisoning (M-8, SEC-ADG-01) -----------------------------------------------------------------------------
        PerDialect("Cache_Rebinds_TenantValueOfTheCurrentRequest", "cache-rebind", d =>
        {
            var env = new ProbeEnv(d);
            Assert.Contains(env.Compile("SELECT id FROM orders", "acme").Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
            var second = env.Compile("SELECT id FROM orders", "ACME");   // a hit: acme and ACME are different tenants
            Assert.Equal(1, env.Engine.CompileCache.Stats.Hits);
            Assert.Contains(second.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "ACME"));
            Assert.DoesNotContain(second.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
        });

        PerDialect("Cache_Rebinds_PolicyValueOfTheCurrentPrincipal", "cache-rebind", d =>
        {
            var env = new ProbeEnv(d);
            env.RegionPolicy("EU");
            env.Compile("SELECT id FROM orders");
            env.RegionPolicy("US");   // the second principal of the same tenant has another consent value
            var second = env.Compile("SELECT id FROM orders");
            Assert.Contains(second.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "US"));
            Assert.DoesNotContain(second.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        });

        Add("Cache_Rebind_EscapesLikePolicyValue", "cache-rebind", () =>
        {
            // SQLE-10: the template of a policy LIKE keeps the escape flag; a hit escapes the value of the current request.
            var statement = new SelectStatement(null,
                new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                    new NamedTableSource(new SqlQualifiedName("t"), null),
                    new LikeExpression(new ColumnReference(new SqlQualifiedName("n")), new PolicyParameterExpression("pat", SqlParameterType.String)), null, null), null, null);
            var template = CompiledSqlTemplate.From("key",
                TrinoSqlEngine.Tests.Compiler.CompilerTestHelpers.GenerateSqlServer(statement, new Dictionary<string, PolicyValue> { ["pat"] = new("a", SqlParameterType.String) }),
                ImmutableArray<TableDependency>.Empty);
            var hit = template.Rebind(new Dictionary<string, PolicyValue> { ["pat"] = new("50%_", SqlParameterType.String) });
            Assert.Equal("50\\%\\_", Assert.Single(hit.Parameters).Value);
        });

        Add("Cache_KeyContainsTheTargetDialect", "cache-key", () =>
        {
            // PostgreSQL and DuckDB share one catalog shape, so only the key (not the table fingerprint) can tell them apart.
            var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
            env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(), CancellationToken.None);
            var other = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(dialect: TargetSqlDialect.DuckDb), CancellationToken.None);
            Assert.Equal(TargetSqlDialect.DuckDb, other.Dialect);
            Assert.Contains("ENCODE(", other.Sql);
            Assert.DoesNotContain("TEXTSEND", other.Sql);
        });

        Add("Cache_KeyContainsTheRowLimit", "cache-key", () =>
        {
            var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
            var open = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(enforcedMaxRows: 1000), CancellationToken.None);
            var capped = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(enforcedMaxRows: 10), CancellationToken.None);
            Assert.NotEqual(open.Sql, capped.Sql);
        });

        Add("Cache_KeyContainsTheStatementPermissions", "cache-key", () =>
        {
            var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
            env.Engine.Compile("DELETE FROM orders WHERE id = 1".AsMemory(), env.Request(), CancellationToken.None);
            Assert.Throws<SqlCompileNotSupportedException>(() =>
                env.Engine.Compile("DELETE FROM orders WHERE id = 1".AsMemory(), env.Request(statements: StatementPermissions.ReadOnly), CancellationToken.None));
        });

        Add("Cache_KeyContainsTheTokenGuards", "cache-key", () =>
        {
            var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
            const string withComment = "SELECT id /* note */ FROM orders";
            env.Engine.Compile(withComment.AsMemory(), env.Request(guards: SqlTokenSecurityOptions.None), CancellationToken.None);
            Assert.ThrowsAny<Exception>(() => env.Engine.Compile(withComment.AsMemory(), env.Request(guards: SqlTokenSecurityOptions.Strict), CancellationToken.None));
        });

        Add("Cache_KeyContainsTheCompilerAndCapabilityVersion", "cache-key", () =>
        {
            var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
            string key = CompileCacheKey.Material(env.Request(), "SELECT 1", DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql));
            Assert.Contains(CompilerInfo.Version, key);
            Assert.Contains(DialectCapabilities.TableVersion, key);
        });

        // ---- error sanitizer (CR-ADG-34, SEC-ADG-16) --------------------------------------------------------------------------
        PerDialect("ErrorSanitizer_NeverEchoesTheProviderMessage", "error-sanitizer", d =>
        {
            var provider = new InvalidOperationException("Violation of PRIMARY KEY constraint 'PK_orders'. Duplicate key value (acme, 17) in table dbo.Orders");
            var mapped = DmlErrorSanitizer.Map(d, provider);
            Assert.DoesNotContain("acme", mapped.Message);
            Assert.DoesNotContain("Orders", mapped.Message);
            Assert.DoesNotContain("PRIMARY", mapped.Message);
            Assert.Equal(GovernedSqlErrorCodes.FixedMessage(mapped.Code), mapped.Message);
            Assert.Null(mapped.InnerException);
        });

        // ---- ${ variable substitution (SEC-ADG-10) ------------------------------------------------------------------------------
        Add("VariableSubstitution_RejectedByTheTokenGuard", "variable-substitution", () =>
        {
            var engine = new FastSqlEngine();
            Assert.ThrowsAny<Exception>(() => engine.Parse("SELECT id FROM orders WHERE status = '${secret}'".AsMemory(), SqlTokenSecurityOptions.Strict));
        });

        Add("VariableSubstitution_RejectedInADatabricksIdentifier", "variable-substitution", () =>
        {
            var statement = Compiler.CompilerTestHelpers.Build("SELECT 1 FROM \"a${b}\"");
            Assert.Throws<SecurityException>(() => Compiler.CompilerTestHelpers.Generate(TargetSqlDialect.Databricks, statement));
        });

        // ---- limits -------------------------------------------------------------------------------------------------------------
        Add("InListLimit_Oracle_1001ItemsAreRejected_1000Pass", "in-list-limit", () =>
        {
            var env = new ProbeEnv(TargetSqlDialect.Oracle);
            string List(int n) => string.Join(", ", Enumerable.Range(1, n));
            Assert.NotEmpty(env.Compile($"SELECT id FROM orders WHERE id IN ({List(1000)})").Sql);
            var ex = Assert.Throws<SqlLimitExceededException>(() => env.Compile($"SELECT id FROM orders WHERE id IN ({List(1001)})"));
            Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
        });

        // ---- client lists (REQ-SQL-128, SEC-128-01, -02, -10) ----------------------------------------------------------------
        Add("ClientList_EmptyNotInDelete_IsRejectedAsUnfiltered", "client-list-order", () =>
        {
            var h = new Compiler.ClientListHarness();
            Assert.Throws<UnfilteredDmlException>(() => h.Compile(TargetSqlDialect.PostgreSql, "DELETE FROM orders WHERE id NOT IN (__param_e)",
                Compiler.ClientListHarness.Shapes(("e", 0)), StatementPermissions.Delete));
        });

        Add("ClientList_ListInScalarPosition_IsRejected", "client-list-position", () =>
        {
            var h = new Compiler.ClientListHarness();
            Assert.Throws<ClientListException>(() => h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = __param_l",
                Compiler.ClientListHarness.Shapes(("l", 3))));
        });

        Add("ClientList_RepeatedUse_IsCappedPerStatement", "client-list-cap", () =>
        {
            var h = new Compiler.ClientListHarness();
            string sql = "SELECT id FROM orders WHERE " + string.Join(" OR ", Enumerable.Repeat("id IN (__param_l)", 11));
            Assert.Throws<ClientListException>(() => h.Compile(TargetSqlDialect.PostgreSql, sql, Compiler.ClientListHarness.Shapes(("l", 1000))));
        });

        Add("ClientList_LongerListThanCompiled_IsRefusedNotTruncated", "client-list-count", () =>
        {
            var h = new Compiler.ClientListHarness();
            var compiled = h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id IN (__param_ids)", Compiler.ClientListHarness.Shapes(("ids", 2)));
            var longer = ClientListValue.Create(new object[] { 1L, 2L, 3L }, SqlParameterType.Int64);
            Assert.Throws<ClientListException>(() => CompiledSqlBinders.For(TargetSqlDialect.PostgreSql)
                .Bind(new Compiler.BinderMutationTests.RecCommand(), compiled, new Dictionary<string, object?> { ["ids"] = longer }));
        });

        PerDialect("ExecutionStack_Exhaustion_FailsClosed", "execution-stack", d =>
        {
            foreach (int at in new[] { 1, 4, 12 })
            {
                var env = new ProbeEnv(d);
                using var _ = MutantContext.Overlay(exhaustStackAtGuardCall: at);
                Assert.ThrowsAny<Exception>(() => env.Compile("SELECT id FROM orders WHERE id IN (SELECT orderid FROM entitlements)"));
                Assert.Equal(0, env.Engine.CompileCache.Stats.Entries);
            }
        }, new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql });

        all.AddRange(ActivationProbes.Build());
        return all;
    }

    /// <summary>A rewriter used as the injector fault (it returns the statement unchanged when nothing matches).</summary>
    internal static Func<SqlStatement, SqlStatement> Rewrite(TrinoSqlEngine.Ast.Visitors.SqlAstRewriter rewriter) =>
        statement => (SqlStatement)rewriter.Visit(statement);

    private static List<Expression> Conjuncts(Expression expression) => expression switch
    {
        BinaryExpression { Operator: BinaryOperator.And } and => Conjuncts(and.Left).Concat(Conjuncts(and.Right)).ToList(),
        ParenthesizedExpression paren => Conjuncts(paren.Expression),
        _ => new List<Expression> { expression }
    };

    private static SqlStatement Unsecured() =>
        new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Orders", true) }), null),
                null, null, null), null, null);

    private static CompiledSql Checked(string sql, int? expected, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert, ImmutableArray<SecurityPredicateId>.Empty, "test")
        {
            ExpectedAffectedRows = expected
        };

    private static SqliteConnection OpenTable()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (a INTEGER)";
        create.ExecuteNonQuery();
        return connection;
    }

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t";
        return (long)command.ExecuteScalar()!;
    }
}
