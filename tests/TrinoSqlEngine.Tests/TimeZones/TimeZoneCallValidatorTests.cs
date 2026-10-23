namespace TrinoSqlEngine.Tests.TimeZones;

using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Tests.Security;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.TimeZones.TimeZoneTestSupport;

/// <summary>REQ-SQL-130 WP-2: the validator runs for every dialect on the user tree, in the fixed order of A-3, with fixed messages.</summary>
public sealed class TimeZoneCallValidatorTests
{
    private const string Good = "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e";

    // ---- T-8 order of checks ----

    [Theory]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void T8_AnUnsupportedDialectWinsOverEverythingElse(TargetSqlDialect dialect)
    {
        // wrong arity, unknown zone, not available: the dialect decides first
        var ex = Assert.Throws<SqlTimeZoneException>(() => Compile(dialect, "SELECT convert_timezone('Mars/Base', 1) FROM dbo.Events", available: false));
        Assert.Equal(GovernedSqlErrorCodes.FunctionUnsupportedDialect, ex.Code);
    }

    [Fact]
    public void T8_Sqlite_IsAPolicyViolationBeforeAnyZoneCheck()
    {
        // GAP-019: convert_timezone is not on the SQLite allowlist, so the function policy answers first.
        Assert.ThrowsAny<System.Security.SecurityException>(() => Compile(TargetSqlDialect.Sqlite, "SELECT convert_timezone('Mars/Base', 1) FROM dbo.Events", available: false));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T8_AvailabilityWinsOverShapeZoneAndTimestamp(TargetSqlDialect dialect) =>
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneUnavailable, Rejected(dialect, "SELECT convert_timezone('Mars/Base', 1) FROM dbo.Events", available: false));

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T8_ShapeWinsOverZone(TargetSqlDialect dialect) =>
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, "SELECT convert_timezone('Mars/Base', 'UTC') FROM dbo.Events"));

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T8_ZoneWinsOverTimestamp(TargetSqlDialect dialect) =>
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Rejected(dialect, "SELECT convert_timezone('Mars/Base', 'UTC', e.OccurredAtTz) FROM dbo.Events e"));

    // ---- T-9 literal zones ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T9_AValidLiteralPairPasses(TargetSqlDialect dialect) => Assert.NotNull(Compile(dialect, Good));

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T9_AnUnknownLiteralIsInvalidAndTheMessageEchoesNothing(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<SqlTimeZoneException>(() => Compile(dialect, "SELECT convert_timezone('Europe/Evil', 'UTC', e.OccurredAt) FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, ex.Code);
        Assert.Equal("The time zone is not supported.", ex.Message);
        Assert.DoesNotContain("Evil", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Events", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Europe/Berlin'; DROP TABLE t--")]
    [InlineData("Europe/Berlin/*x*/")]
    [InlineData("' OR 1=1--")]
    [InlineData("Europe/Berlin ")]
    [InlineData("europe/berlin")]
    [InlineData("EST")]
    [InlineData("Еurope/Berlin")]
    [InlineData("{{x}}")]
    [InlineData("%")]
    [InlineData("\\")]
    [InlineData("")]
    public void ST_130_1_HostileZoneLiteralsAreRejectedAtCompileTimeInEveryDialect(string zone)
    {
        string literal = "'" + zone.Replace("'", "''", StringComparison.Ordinal) + "'";
        foreach (var dialect in Dialects)
        {
            foreach (string sql in new[]
                     {
                         $"SELECT convert_timezone({literal}, 'UTC', e.OccurredAt) FROM dbo.Events e",
                         $"SELECT convert_timezone('UTC', {literal}, e.OccurredAt) FROM dbo.Events e"
                     })
            {
                // The token guards refuse comment markers, brackets and backslash escapes before the validator; everything else is an invalid zone.
                var failure = Record.Exception(() => Compile(dialect, sql));
                Assert.NotNull(failure);
                if (failure is SqlTimeZoneException tz)
                {
                    Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, tz.Code);
                    if (zone.Trim().Length > 0) Assert.DoesNotContain(zone.Trim(), tz.Message, StringComparison.Ordinal);
                }
                else
                {
                    Assert.IsType<Antlr4.Runtime.Misc.ParseCanceledException>(failure);
                }
            }
        }
    }

    [Fact]
    public void ST_130_1_AnOverlongAndANulZoneLiteralAreRejected()
    {
        foreach (var dialect in Dialects)
        {
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Rejected(dialect, $"SELECT convert_timezone('{new string('a', 65)}', 'UTC', e.OccurredAt) FROM dbo.Events e"));
        }

        // A NUL cannot be written in a SQL text literal, but a hand-built AST value is refused just as well.
        var statement = CompilerTestHelpers.Build("SELECT convert_timezone('Europe/Berlin', 'UTC', e.OccurredAt) FROM dbo.Events e");
        var tampered = new ZoneLiteralReplacer("Europe/Berlin", "Europe/Berlin\0").Visit(statement);
        var ex = Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversionTestApi.ValidateGoverned(TargetSqlDialect.PostgreSql, (TrinoSqlEngine.Ast.Nodes.SqlStatement)tampered));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, ex.Code);
    }

    // ---- T-10 non-literal zones ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T10_NonLiteralZonesAreArgumentErrors(TargetSqlDialect dialect)
    {
        string[] zones =
        [
            "e.Label", "'Europe/' || 'Berlin'", "lower('UTC')", "CAST('UTC' AS varchar)", "(SELECT 'UTC')", "?", "NULL", "5", "CASE WHEN 1 = 1 THEN 'UTC' ELSE 'UTC' END",
            "concat('Europe/', 'Berlin')", "TRUE"
        ];
        foreach (string zone in zones)
        {
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, $"SELECT convert_timezone({zone}, 'UTC', e.OccurredAt) FROM dbo.Events e"));
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, $"SELECT convert_timezone('UTC', {zone}, e.OccurredAt) FROM dbo.Events e"));
        }
    }

    // ---- T-11 parameter zones ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T11_SyntheticParameterZonesPassTheValidator(TargetSqlDialect dialect) =>
        Assert.NotNull(Compile(dialect, "SELECT convert_timezone(__param_from, __param_to, e.OccurredAt) FROM dbo.Events e"));

    // ---- T-12 shape ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T12_WrongShapeIsAnArgumentError(TargetSqlDialect dialect)
    {
        string[] calls =
        [
            "convert_timezone('UTC', 'Europe/Berlin')",
            "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt, e.OccurredAt)",
            "convert_timezone()",
            "convert_timezone(DISTINCT 'UTC', 'Europe/Berlin', e.OccurredAt)",
            "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FILTER (WHERE e.Id > 1)",
            "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) OVER (ORDER BY e.Id)"
        ];
        foreach (string call in calls)
        {
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, $"SELECT {call} FROM dbo.Events e"));
        }
    }

    // ---- T-13 / T-14 timestamp forms ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T13_AcceptedTimestampForms(TargetSqlDialect dialect)
    {
        string[] forms =
        [
            "e.OccurredAt", "dbo.Events.OccurredAt", "CAST(e.Label AS timestamp)", "CAST(e.Label AS timestamp(3))", "TIMESTAMP '2026-03-29 01:00:00'",
            "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt)", "(e.OccurredAt)", "CAST(e.OccurredAtTz AS timestamp)"
        ];
        foreach (string form in forms)
        {
            string sql = form.StartsWith("dbo.Events", StringComparison.Ordinal)
                ? $"SELECT convert_timezone('UTC', 'Europe/Berlin', {form}) FROM dbo.Events"
                : $"SELECT convert_timezone('UTC', 'Europe/Berlin', {form}) FROM dbo.Events e";
            Assert.NotNull(Compile(dialect, sql));
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void T13_TryCastToATimestampIsAccepted(TargetSqlDialect dialect) =>
        Assert.NotNull(Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', TRY_CAST(e.Label AS timestamp)) FROM dbo.Events e"));

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T14_RejectedTimestampForms(TargetSqlDialect dialect)
    {
        string[] forms =
        [
            "e.OccurredAtTz", "e.OccurredOn", "e.Label", "@p", "current_timestamp", "e.OccurredAt + INTERVAL '1' DAY", "CAST(e.OccurredAt AS timestamp with time zone)",
            "CAST(e.OccurredAt AS date)", "CAST(e.OccurredAt AS varchar)", "NULL", "'2026-03-29 01:00:00'", "DATE '2026-03-29'", "(SELECT e2.OccurredAt FROM dbo.Other e2)",
            "COALESCE(e.OccurredAt, e.OccurredAt)", "CAST((SELECT e2.OccurredAt FROM dbo.Other e2) AS timestamp)", "e.NoSuchColumn"
        ];
        foreach (string form in forms)
        {
            string sql = form.Contains('@', StringComparison.Ordinal)
                ? "SELECT convert_timezone('UTC', 'Europe/Berlin', __param_ts) FROM dbo.Events e"
                : $"SELECT convert_timezone('UTC', 'Europe/Berlin', {form}) FROM dbo.Events e";
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, sql));
        }
    }

    [Fact]
    public void T14_TheSqlServerTimestampTypeIsARowversionAndNeverATime() =>
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(TargetSqlDialect.SqlServer, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.Version) FROM dbo.Events e"));

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T14_UnresolvedCteAndDerivedTableColumnsAreRejectedAsBareTimestamps(TargetSqlDialect dialect)
    {
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect,
            "WITH c AS (SELECT e.OccurredAt AS ts FROM dbo.Events e) SELECT convert_timezone('UTC', 'Europe/Berlin', c.ts) FROM c"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect,
            "SELECT convert_timezone('UTC', 'Europe/Berlin', d.ts) FROM (SELECT e.OccurredAt AS ts FROM dbo.Events e) d"));
        // an unqualified name is decidable only when every source is a catalog table that has the column
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect,
            "SELECT convert_timezone('UTC', 'Europe/Berlin', OccurredAt) FROM dbo.Events a JOIN dbo.Other b ON a.Id = b.Id"));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T14_AnUnqualifiedColumnOfASingleCatalogTableResolves(TargetSqlDialect dialect) =>
        Assert.NotNull(Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', OccurredAt) FROM dbo.Events"));

    // ---- T-15 operator form ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T15_TheAtTimeZoneOperatorStaysRejected(TargetSqlDialect dialect)
    {
        Assert.ThrowsAny<Exception>(() => Compile(dialect, "SELECT e.OccurredAt AT TIME ZONE 'UTC' FROM dbo.Events e"));
        Assert.ThrowsAny<Exception>(() => Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt AT TIME ZONE 'UTC') FROM dbo.Events e"));
    }

    // ---- ST-130-6 name spellings ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_6_QuotedAndMixedCaseSpellingsReachTheValidator(TargetSqlDialect dialect)
    {
        // A delimited function name is refused by the builder (gate 2); the validator itself ignores quoting (hand-built tree below).
        Assert.ThrowsAny<System.Security.SecurityException>(() => Compile(dialect, "SELECT \"CONVERT_TIMEZONE\"('Mars/Base', 'UTC', e.OccurredAt) FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Rejected(dialect, "SELECT Convert_TimeZone('Mars/Base', 'UTC', e.OccurredAt) FROM dbo.Events e"));
        Assert.NotNull(Compile(dialect, "SELECT CONVERT_TIMEZONE('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e"));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_6_QualifiedSpellingsNeverReachTheGeneratorAsAPassThrough(TargetSqlDialect dialect)
    {
        foreach (string name in new[] { "dbo.convert_timezone", "main.convert_timezone", "x.\"convert_timezone\"", "a.b.convert_timezone" })
        {
            var ex = Record.Exception(() => Compile(dialect, $"SELECT {name}('Mars/Base', 'UTC', e.OccurredAtTz) FROM dbo.Events e"));
            Assert.NotNull(ex);
            Assert.DoesNotContain("CONVERT_TIMEZONE(", ex!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ST_130_6_TheValidatorIgnoresQuotingOfTheName()
    {
        var statement = CompilerTestHelpers.Build("SELECT convert_timezone('Mars/Base', 'UTC', e.OccurredAt) FROM dbo.Events e");
        var quoted = (TrinoSqlEngine.Ast.Nodes.SqlStatement)new FunctionNameReplacer(
            new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName([new TrinoSqlEngine.Ast.Nodes.SqlIdentifier("CONVERT_TIMEZONE", true)])).Visit(statement);
        var ex = Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversionTestApi.ValidateGoverned(TargetSqlDialect.PostgreSql, quoted));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, ex.Code);
    }

    [Fact]
    public void ST_130_6_AQualifiedNameThatReachesTheValidatorFailsValidation()
    {
        var statement = CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e");
        var qualified = (TrinoSqlEngine.Ast.Nodes.SqlStatement)new FunctionNameReplacer(new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName("dbo", "convert_timezone")).Visit(statement);
        var ex = Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversionTestApi.ValidateGoverned(TargetSqlDialect.PostgreSql, qualified));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, ex.Code);
    }

    // ---- the pass runs when the text spells the function ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void TheTimeZonePassRunsOnlyForAStatementThatSpellsTheFunction(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine();   // a fresh engine: a cache hit reports no pass
        var without = new List<string>();
        using (CompilerHooks.Use(new MutantHooks { OnPass = without.Add }))
        {
            engine.Compile("SELECT e.Id FROM dbo.Events e".AsMemory(), Request(dialect, available: true), CancellationToken.None);
        }

        var with = new List<string>();
        using (CompilerHooks.Use(new MutantHooks { OnPass = with.Add }))
        {
            engine.Compile(Good.AsMemory(), Request(dialect, available: true), CancellationToken.None);
        }

        Assert.DoesNotContain("timezone", without);
        Assert.Contains("timezone", with);
        Assert.True(with.IndexOf("timezone") > with.IndexOf("validate") && with.IndexOf("timezone") < with.IndexOf("simplify"));
    }

    // ---- ST-130-7 related names are not admitted ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_7_TheRelatedZoneFunctionsAreRejectedByTheAllowlist(TargetSqlDialect dialect)
    {
        foreach (string call in new[] { "timezone('UTC', e.OccurredAt)", "at_timezone(e.OccurredAt, 'UTC')", "with_timezone(e.OccurredAt, 'UTC')", "switchoffset(e.OccurredAt, '+01:00')",
                     "todatetimeoffset(e.OccurredAt, '+01:00')", "timezone_hour(e.OccurredAt)" })
        {
            Assert.ThrowsAny<Exception>(() => Compile(dialect, $"SELECT {call} FROM dbo.Events e"));
        }
    }

    // ---- ST-130-6 full compile path over raw SQL text (SEC-130 review finding: not an AST replacement) ----

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_6_DelimitedAndLookalikeSpellingsOfTheNameAreRejectedOverTheFullCompilePath(TargetSqlDialect dialect)
    {
        string[] names =
        [
            "\"convert_timezone\"", "`convert_timezone`", "[convert_timezone]", "\"CONVERT_TIMEZONE\"",
            "\uFF43\uFF4F\uFF4E\uFF56\uFF45\uFF52\uFF54\uFF3F\uFF54\uFF49\uFF4D\uFF45\uFF5A\uFF4F\uFF4E\uFF45"   // fullwidth lookalike
        ];
        foreach (string name in names)
        {
            string sql = $"SELECT {name}('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e";
            var ex = Record.Exception(() => Compile(dialect, sql));
            Assert.NotNull(ex);
            Assert.DoesNotContain("CONVERT_TIMEZONE(", ex!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_6_AMixedCaseSpellingReachesTheValidatorOverTheFullCompilePath(TargetSqlDialect dialect)
    {
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Rejected(dialect, "SELECT CoNvErT_TiMeZoNe('Mars/Base', 'UTC', e.OccurredAt) FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, "SELECT CoNvErT_TiMeZoNe('UTC', 'Europe/Berlin', e.OccurredAtTz) FROM dbo.Events e"));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_10_ACallInjectedIntoTheAstOfAStatementThatDoesNotSpellTheNameIsRefusedByTheGeneratorGuard(TargetSqlDialect dialect)
    {
        // The text has no "convert_timezone", so the validator pass is skipped and TimeZoneCallsValidated stays false; a faulty injector that adds the
        // call afterwards must not be able to emit it.
        string sql = "SELECT e.Id AS injected_guard_probe FROM dbo.Events e";   // unique text: a plan-cache hit would skip the hook
        var passes = new List<string>();
        using var _ = CompilerHooks.Use(new MutantHooks
        {
            OnPass = passes.Add,
            FaultyInjector = secured =>
            {
                var select = (SelectStatement)secured;
                var spec = (QuerySpecification)select.Body;
                var call = new FunctionCallExpression(new SqlQualifiedName("convert_timezone"), [
                    new LiteralExpression("UTC", LiteralType.String), new LiteralExpression("Europe/Berlin", LiteralType.String),
                    new ColumnReference(new SqlQualifiedName("e", "OccurredAt"))]);
                return select with { Body = spec with { Projections = [.. spec.Projections, new ColumnSelectItem(call, null)] } };
            }
        });

        var ex = Record.Exception(() => Compile(dialect, sql));

        Assert.DoesNotContain("timezone", passes);
        Assert.NotNull(ex);
        Assert.DoesNotContain("CONVERT_TIMEZONE(", ex!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SqlTimeZoneException>(ex);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, ((SqlTimeZoneException)ex).Code);
    }
}
