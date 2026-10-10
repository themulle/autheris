using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-A7 proofs that do not depend on the injector being right: the coverage verifier and the emitted-text checker reject a
/// faulty DML tree (mutation tests through the Debug-only seam), the MERGE clause set and the grammar stay closed (SEC-ADG-08 a),
/// the SQL Server MERGE terminator is accepted only at its registered position (SEC-ADG-08 c), and the capability matrix of the
/// DML classes is pinned.
/// </summary>
public class DmlCoverageTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public DmlCoverageTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
    }

    public static IEnumerable<object[]> Dialects() => DmlCompileTests.DialectData();

    private CompiledSql Compile(TargetSqlDialect dialect, string sql, DmlGuardOptions? dml = null) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            Dml = dml ?? DmlGuardOptions.Strict
        }
    }, CancellationToken.None);

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static NamedTableSource Canonical(string alias = "") => new(
        new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Orders", true) }),
        alias.Length == 0 ? null : new SqlIdentifier(alias));

#if DEBUG
    private void Mutant(TargetSqlDialect dialect, string sql, Func<SqlStatement, SqlStatement> mutate)
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyInjector = s => mutate(s) });
        Assert.Throws<SecurityCoverageException>(() => Compile(dialect, sql));
        Assert.Equal(0, _engine.CompileCache.Stats.Entries);   // never cached unverified
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Delete_WithoutTheInjectedPredicates_IsRejectedByTheVerifier(TargetSqlDialect dialect) =>
        Mutant(dialect, "DELETE FROM orders WHERE id = 1", s =>
            ((DeleteStatement)s) with { Where = new BinaryExpression(Col("id"), BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer)) });

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Update_WithTheTenantPredicateRemoved_OrWithTheWrongScope_IsRejectedByTheVerifier(TargetSqlDialect dialect)
    {
        Mutant(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", s =>
            ((UpdateStatement)s) with { Where = new ParenthesizedExpression(new BinaryExpression(Col("id"), BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer))) });
        Mutant(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", s =>
        {
            var u = (UpdateStatement)s;
            var tenant = new SecurityPredicateExpression(new LiteralExpression(true, LiteralType.Boolean), new SecurityPredicateId("dbo.Orders", 0), SecurityScope.Root);
            return u with { Where = new BinaryExpression(((BinaryExpression)u.Where!).Left, BinaryOperator.And, tenant) };
        });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Update_WithAnAddedTenantOrMaskedAssignment_IsRejectedByTheVerifier(TargetSqlDialect dialect)
    {
        Mutant(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", s =>
        {
            var u = (UpdateStatement)s;
            return u with { Assignments = u.Assignments.Append(new UpdateAssignment(new SqlIdentifier("TenantId", true), new LiteralExpression("other", LiteralType.String))).ToList() };
        });
        Mutant(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", s =>
        {
            var u = (UpdateStatement)s;
            return u with { Assignments = u.Assignments.Append(new UpdateAssignment(new SqlIdentifier("Email", true), new LiteralExpression("x", LiteralType.String))).ToList() };
        });
        Mutant(dialect, "UPDATE orders SET status = 'x' WHERE id = 1", s =>
        {
            var u = (UpdateStatement)s;
            return u with { Assignments = new[] { new UpdateAssignment(new SqlIdentifier("Status", true), Col("Email")) } };
        });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Insert_WithTheTenantReplacedByALiteral_OrAColumnMissing_IsRejectedByTheVerifier(TargetSqlDialect dialect)
    {
        Mutant(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')", s =>
        {
            var i = (InsertStatement)s;
            var values = (ValuesQueryBody)i.Source;
            return i with { Source = values with { Rows = new[] { new RowValueExpression(new Expression[] { new LiteralExpression(1L, LiteralType.Integer), new LiteralExpression("other", LiteralType.String) }) } } };
        });
        Mutant(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')", s =>
        {
            var i = (InsertStatement)s;
            return i with { Columns = new[] { i.Columns![0] } };
        });
        Mutant(dialect, "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')", s =>
        {
            var i = (InsertStatement)s;
            return i with { Columns = null };
        });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Merge_WithoutTheOnPredicate_WithTheInsertTenantReplaced_OrWithAnUnknownClause_IsRejectedByTheVerifier(TargetSqlDialect dialect)
    {
        const string sql = "MERGE INTO orders t USING entitlements s ON t.id = s.orderid " +
                           "WHEN NOT MATCHED THEN INSERT (id, tenantid) VALUES (s.id, 'acme')";
        Mutant(dialect, sql, s => ((MergeStatement)s) with { On = new BinaryExpression(Col("id"), BinaryOperator.Equal, Col("id")) });
        Mutant(dialect, sql, s =>
        {
            var m = (MergeStatement)s;
            var insert = (MergeInsertClause)m.Clauses[0];
            return m with { Clauses = new MergeClause[] { insert with { Values = new Expression[] { insert.Values[0], new LiteralExpression("other", LiteralType.String) } } } };
        });
        Mutant(dialect, sql, s => ((MergeStatement)s) with { Clauses = ((MergeStatement)s).Clauses.Append(new NotMatchedBySourceDelete()).ToList() });
        Mutant(dialect, sql, s => ((MergeStatement)s) with { Target = ((MergeStatement)s).Target with { Alias = null } });
    }

    private sealed record NotMatchedBySourceDelete() : MergeClause((Expression?)null);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void FaultyEmitter_AddingASemicolonToADml_IsRejectedByTheEmittedTextChecker(TargetSqlDialect dialect)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM orders WHERE id = 1",
                     "UPDATE orders SET status = 'x' WHERE id = 1",
                     "INSERT INTO orders (id, tenantid) VALUES (1, 'acme')",
                     "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE"
                 })
        {
            using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyEmitter = text => text + ";" + (text.EndsWith(';') ? "" : " SELECT 1") });
            Assert.Throws<EmittedSqlInvariantViolationException>(() => Compile(dialect, sql));
        }
    }

    [Fact]
    public void FaultyEmitter_InsertingASemicolonBeforeTheMergeTerminator_IsRejected()
    {
        using var _ = CompilerTestSeams.Use(new CompilerTestSeams { FaultyEmitter = text => text.Replace(" WHEN ", "; DROP TABLE x; MERGE WHEN ", StringComparison.Ordinal) });
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Compile(TargetSqlDialect.SqlServer, "MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE"));
    }
#endif

    // ---- the emitted-text checker and the MERGE terminator (SEC-ADG-08 c) ----

    private static readonly ImmutableArray<BoundParameter> NoParams = ImmutableArray<BoundParameter>.Empty;

    private static void Check(string sql, TargetSqlDialect dialect, SqlStatementClass cls, params EmittedRange[] ranges) =>
        EmittedSqlInvariantChecker.Check(sql, dialect, NoParams, ranges, structuralOnly: false, cls);

    [Fact]
    public void Checker_AllowsSemicolon_OnlyAtRegisteredFinalPosition_SqlServerMerge()
    {
        const string sql = "MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE;";
        var terminator = new EmittedRange(sql.Length - 1, 1, EmittedRangeKind.StatementTerminator);
        Check(sql, TargetSqlDialect.SqlServer, SqlStatementClass.Merge, terminator);   // registered at the final position: accepted
    }

    [Fact]
    public void Checker_RejectsAnUnregisteredSemicolon_EvenOnAnSqlServerMerge()
    {
        const string sql = "MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE;";
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql, TargetSqlDialect.SqlServer, SqlStatementClass.Merge));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, SqlStatementClass.Merge)]
    [InlineData(TargetSqlDialect.DuckDb, SqlStatementClass.Merge)]
    [InlineData(TargetSqlDialect.Oracle, SqlStatementClass.Merge)]
    [InlineData(TargetSqlDialect.Databricks, SqlStatementClass.Merge)]
    [InlineData(TargetSqlDialect.SqlServer, SqlStatementClass.Select)]
    [InlineData(TargetSqlDialect.SqlServer, SqlStatementClass.Update)]
    [InlineData(TargetSqlDialect.SqlServer, SqlStatementClass.Delete)]
    [InlineData(TargetSqlDialect.SqlServer, SqlStatementClass.Insert)]
    public void Checker_RejectsARegisteredTerminator_ForEveryOtherDialectAndStatementClass(TargetSqlDialect dialect, SqlStatementClass cls)
    {
        const string sql = "SELECT [a] FROM [t];";
        var terminator = new EmittedRange(sql.Length - 1, 1, EmittedRangeKind.StatementTerminator);
        Assert.Throws<EmittedSqlInvariantViolationException>(() => Check(sql, dialect, cls, terminator));
    }

    [Fact]
    public void Checker_RejectsATerminator_ThatIsNotTheLastCharacter_OrIsRegisteredTwice()
    {
        const string sql = "MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE; DELETE FROM [t]";
        int first = sql.IndexOf(';');
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(sql, TargetSqlDialect.SqlServer, SqlStatementClass.Merge, new EmittedRange(first, 1, EmittedRangeKind.StatementTerminator)));

        const string two = "MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE;;";
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            Check(two, TargetSqlDialect.SqlServer, SqlStatementClass.Merge,
                new EmittedRange(two.Length - 2, 1, EmittedRangeKind.StatementTerminator), new EmittedRange(two.Length - 1, 1, EmittedRangeKind.StatementTerminator)));
    }

    // ---- closed sets (SEC-ADG-08 a) ----

    [Fact]
    public void MergeClauseTypes_AreClosed()
    {
        var clauseTypes = typeof(MergeClause).Assembly.GetTypes()
            .Where(t => typeof(MergeClause).IsAssignableFrom(t) && t != typeof(MergeClause))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { nameof(MergeDeleteClause), nameof(MergeInsertClause), nameof(MergeUpdateClause) }, clauseTypes);
    }

    [Fact]
    public void Grammar_HasNoNotMatchedBySource_AndNoByTarget()
    {
        string grammar = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TrinoSqlEngine", "SqlBase.g4"));
        int start = grammar.IndexOf("mergeCase", grammar.IndexOf("mergeCase\n", StringComparison.Ordinal), StringComparison.Ordinal);
        string rule = grammar[start..grammar.IndexOf(';', start)];
        Assert.DoesNotMatch(@"\bBY\b", rule);
        Assert.DoesNotMatch(@"\bSOURCE\b", rule);
        Assert.DoesNotMatch(@"\bTARGET\b", rule);

        // And no other statement rule grows a RETURNING or OUTPUT clause or an UPDATE ... FROM / DELETE ... USING form.
        foreach (var statementRule in new[] { "#update", "#delete", "#insertInto", "#merge" })
        {
            int at = grammar.IndexOf(statementRule, StringComparison.Ordinal);
            int alternative = grammar.LastIndexOf("\n    | ", at, StringComparison.Ordinal);
            string text = grammar[alternative..at];
            Assert.DoesNotContain("RETURNING", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OUTPUT", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // ---- the status matrix: a class is compiled only where it is listed (fail closed everywhere else) ----

    public static IEnumerable<object[]> Matrix() => new[]
    {
        new object[] { TargetSqlDialect.SqlServer, StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge },
        new object[] { TargetSqlDialect.DuckDb, StatementPermissions.ReadOnly },
        new object[] { TargetSqlDialect.PostgreSql, StatementPermissions.ReadOnly },
        new object[] { TargetSqlDialect.Databricks, StatementPermissions.ReadOnly },
        new object[] { TargetSqlDialect.Oracle, StatementPermissions.ReadOnly }
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void DmlStatementMatrix_IsPinnedPerDialect(TargetSqlDialect dialect, StatementPermissions expected) =>
        Assert.Equal(expected, DialectCapabilityTable.Default.Get(dialect).DmlStatements);
}
