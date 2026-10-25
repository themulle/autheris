using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.TimeZones;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-B2 (SEC-LB-07 to SEC-LB-11): the allocation work behind the G8 0.5 KB bucket must not weaken any control. A lazy or skipped
/// pass decides from the parsed input or the policy object only, a missing state runs the pass, and the lazily created syntax-error
/// cache is thread-safe, bounded and never holds a success.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class G8AllocationGuardSecurityTests
{
    private static readonly MaskSpec Nullify = new(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

    private static CompileRequest Request(bool maskEmail, bool recursive = false, TargetSqlDialect dialect = TargetSqlDialect.SqlServer)
    {
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Add(Orders);
        filters.NoPolicy.Add(Entitlements);
        filters.NoPolicy.Add(Lookup);
        var masks = new DictMaskProvider();
        if (maskEmail)
        {
            masks.Masks[(Orders, "email")] = Nullify;
        }

        return new CompileRequest
        {
            TargetDialect = dialect,
            AllowExperimentalDialect = true,
            AllowRecursiveCte = recursive,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = filters,
                Masks = masks,
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                TablesWithMaskedColumns = maskEmail
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "dbo.Orders" }
                    : FrozenSet<string>.Empty
            }
        };
    }

    private static CompiledSql Compile(string sql, CompileRequest request) => new FastSqlEngine().Compile(sql.AsMemory(), request, CancellationToken.None);

    // ---- SEC-LB-09: lazy syntax-error cache ----

    [Fact]
    public void SyntaxErrorCache_LazyInit_ConcurrentFirstUse_NoLostOrCrossedEntries()
    {
        var engine = new FastSqlEngine();
        Assert.False(engine.SyntaxErrorCacheInitialized);

        const int workers = 16;
        var statements = Enumerable.Range(0, workers).Select(i => $"SELECT * FROM t{i} WHERE").ToArray();
        var firstMessages = new string[workers];
        using var gate = new Barrier(workers);

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
        {
            gate.SignalAndWait();
            firstMessages[i] = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(statements[i].AsMemory())).Message;
        });

        Assert.True(engine.SyntaxErrorCacheInitialized);
        Assert.Equal(workers, engine.SyntaxErrorCacheCount);
        for (int i = 0; i < workers; i++)
        {
            // every statement is served from its own entry, with its own message
            var again = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(statements[i].AsMemory()));
            Assert.Equal(firstMessages[i], again.Message);
        }

        Assert.Equal(workers, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void SyntaxErrorCache_LazyInit_DoesNotCacheSuccess()
    {
        var engine = new FastSqlEngine();

        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);
        Assert.False(engine.SyntaxErrorCacheInitialized);
        Assert.Equal(0, engine.SyntaxErrorCacheCount);

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("SELECT * FROM WHERE".AsMemory()));
        Assert.True(engine.SyntaxErrorCacheInitialized);
        Assert.Equal(1, engine.SyntaxErrorCacheCount);

        // a null cache is a miss only: the valid statement parses before and after, and is never stored
        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);
        Assert.Equal(1, engine.SyntaxErrorCacheCount);
    }

    // ---- SEC-LB-07/08: passes that may only be skipped on a positive state ----

    [Fact]
    public void MaskGuards_SkippedOnlyWhenNoMasks_MaskStillApplied()
    {
        const string aggregate = "SELECT SUM(email) FROM orders";
        const string projection = "SELECT email FROM orders";

        // no mask in the policy: the guards have nothing to enforce
        Assert.NotNull(Compile(projection, Request(maskEmail: false)));

        // a mask in the policy: the guards and the mask injection run
        Assert.Throws<MaskedAggregationException>(() => Compile("SELECT COUNT(*), email FROM orders GROUP BY email", Request(maskEmail: true)));
        string masked = Compile(projection, Request(maskEmail: true)).Sql;
        string unmasked = Compile(projection, Request(maskEmail: false)).Sql;
        Assert.NotEqual(unmasked, masked);
        Assert.Contains("NULL", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULL", unmasked, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<SecurityException>(() => Compile(aggregate, Request(maskEmail: true)));
    }

    [Fact]
    public void RequiredFilters_SkippedOnlyWhenNone_FilterStillInjected()
    {
        var orders = new TableIdentity("dbo", "orders");
        var regions = new TableIdentity("dbo", "regions");
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(orders,
                ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("tenant_id", "varchar"), new CatalogColumn("region", "varchar")),
                "tenant_id", ImmutableArray.Create("region"), 1),
            new TableCatalogEntry(regions,
                ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("name", "varchar")),
                null, ImmutableArray<string>.Empty, 1)
        }, "dbo");
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };

        // no required filter on the table: nothing to check
        Assert.NotNull(Compile("SELECT id FROM regions", request));

        // a required filter: the statement without it is refused, with it the tenant filter is still injected
        Assert.Throws<RequiredFilterColumnMissingException>(() => Compile("SELECT id FROM orders", request));
        var ok = Compile("SELECT id FROM orders WHERE region = 'EU'", request);
        Assert.Contains("tenant_id", ok.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ok.Parameters.ToArray(), p => Equals(p.Value, "acme"));
    }

    [Fact]
    public void AggregateTypeGuards_StillFireWhenInputPresent()
    {
        var request = Request(maskEmail: false);

        var ex = Assert.Throws<AggregateTypeInvalidException>(() => Compile("SELECT SUM(email) FROM orders", request));
        Assert.Equal(GovernedSqlErrorCodes.AggregationInvalidFunctionForType, ex.Code);

        // no aggregate in the statement: nothing to judge
        Assert.NotNull(Compile("SELECT email FROM orders", request));
        Assert.NotNull(Compile("SELECT SUM(amount) FROM orders", request));
    }

    [Fact]
    public void RecursiveCte_GateStillFires_WhenDisabled()
    {
        const string recursive = "WITH RECURSIVE r (n) AS (SELECT id FROM orders UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r";

        Assert.Throws<SqlCompileNotSupportedException>(() => Compile(recursive, Request(maskEmail: false, recursive: false)));
        Assert.NotNull(Compile(recursive, Request(maskEmail: false, recursive: true)));
        // a statement without the construct compiles with the gate closed
        Assert.NotNull(Compile("SELECT id FROM orders", Request(maskEmail: false, recursive: false)));
    }

    [Theory]
    [InlineData("SELECT email FROM orders")]
    [InlineData("SELECT EMAIL FROM ORDERS")]
    [InlineData("SELECT /* x */ email /* y */ FROM /* z */ orders")]
    [InlineData("SELECT \"Email\" FROM \"Orders\"")]
    [InlineData("SELECT o.eMaIl FROM Orders o")]
    [InlineData("SELECT em/**/ail FROM orders")]
    public void SecurityVisitor_MixedCaseAndCommentedKeywords_StillEnforced(string sql)
    {
        // comment-split, case-variant and quoted inputs are either rejected by the token guards or end up masked and tenant-filtered;
        // none may reach the output as the raw column or without the tenant predicate.
        var request = Request(maskEmail: true);
        var failure = Record.Exception(() => Compile(sql, request));
        if (failure is null)
        {
            var result = Compile(sql, request);
            Assert.Contains("NULL", result.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TenantId", result.Sql, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.True(failure is SecurityException or ParseCanceledException or GovernedSqlException, failure.GetType().Name);
        }
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void TimeZoneValidator_StillRejectsInvalidZone(TargetSqlDialect dialect)
    {
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid,
            TimeZoneTestSupport.Rejected(dialect, "SELECT convert_timezone('Mars/Base', 'UTC', e.OccurredAtTz) FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid,
            TimeZoneTestSupport.Rejected(dialect, "SELECT CONVERT_TIMEZONE('Mars/Base', 'UTC', e.OccurredAtTz) FROM dbo.Events e"));
        Assert.NotNull(TimeZoneTestSupport.Compile(dialect, "SELECT e.Id FROM dbo.Events e"));
    }

    public static IEnumerable<object[]> TokenCorpus() => new[]
    {
        "SELECT Email, ORDERS.Id FROM Orders WHERE Status = 'open'",
        "sElEcT \"Quoted Id\", `back.tick` FROM \"Orders\"",
        "SELECT 'a\\b', 'x${y}', E'q', U&'d\\0041' FROM t",
        "SELECT \u00e9t\u00e9, '\ud83d\ude00', \"\ud83d\ude00\" FROM t",
        "SELECT a[1], b[2] FROM t WHERE x = ARRAY[1, 2]",
        "SELECT transform(a, x -> x + 1) FROM t",
        "SELECT a /* c1 */ FROM t -- tail comment",
        "SELECT /* nested /* inner */ a FROM t",
        "SELECT 1 +",
        "   ",
        string.Empty
    }.Select(sql => new object[] { sql });

    [Theory, MemberData(nameof(TokenCorpus))]
    public void TokenText_Slice_EqualsTokenText_ForEveryToken(string sql)
    {
        var lexer = new SqlBaseLexer(new ZeroCopyCaseInsensitiveStream(sql.AsMemory()));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();

        foreach (var token in tokens.GetTokens())
        {
            var slice = FastSqlEngine.TokenText(token, sql.AsSpan(), out bool hasText);
            Assert.Equal(token.Text is not null, hasText);
            Assert.Equal(token.Text ?? string.Empty, slice.ToString());
        }
    }
}
