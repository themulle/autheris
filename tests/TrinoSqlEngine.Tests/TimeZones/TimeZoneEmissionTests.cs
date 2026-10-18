namespace TrinoSqlEngine.Tests.TimeZones;

using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.TimeZones.TimeZoneTestSupport;

/// <summary>REQ-SQL-130 WP-3: emission (A-6), zone binding (A-4, A-5), generator guards (BC-130-3, BC-130-11, BC-130-12; SEC-130-03, SEC-130-11, SEC-130-12).</summary>
public sealed class TimeZoneEmissionTests
{
    private const string Call = "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt)";

    private static IEnumerable<BoundParameter> Zones(CompiledSql compiled) =>
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.QueryLiteral && p.Value is string);

    // ---- T-17 / T-18 golden shapes ----

    [Fact]
    public void T17_SqlServerEmitsTheCastOfTwoAtTimeZonesWithBoundZones()
    {
        var compiled = Compile(TargetSqlDialect.SqlServer, $"SELECT {Call} AS t FROM dbo.Events e");
        Assert.Matches(@"(?i)CAST\(\(\(\[?e\]?\.\[?OccurredAt\]?\) AT TIME ZONE @p\d+\) AT TIME ZONE @p\d+ AS datetime2\)", compiled.Sql);
        // T-18: SQL Server binds the Windows ids, from_zone first
        var zones = Zones(compiled).ToList();
        Assert.Equal(["UTC", "W. Europe Standard Time"], zones.Select(z => (string)z.Value!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(zones, z => Assert.Equal(SqlParameterType.String, z.Type));
    }

    [Fact]
    public void T17_PostgreSqlEmitsTwoAtTimeZonesWithBoundZones()
    {
        var compiled = Compile(TargetSqlDialect.PostgreSql, $"SELECT {Call} AS t FROM dbo.Events e");
        Assert.Matches(@"(?i)\(\(\(""?e""?\.""?OccurredAt""?\) AT TIME ZONE \$\d+\) AT TIME ZONE \$\d+\)", compiled.Sql);
        Assert.Equal(["Europe/Berlin", "UTC"], Zones(compiled).Select(z => (string)z.Value!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void T17_DuckDbEmitsNestedTimezoneCallsWithBoundZones()
    {
        var compiled = Compile(TargetSqlDialect.DuckDb, $"SELECT {Call} AS t FROM dbo.Events e");
        Assert.Matches(@"(?i)TIMEZONE\(\$\d+, TIMEZONE\(\$\d+, ""?e""?\.""?OccurredAt""?\)\)", compiled.Sql);
        Assert.Equal(["Europe/Berlin", "UTC"], Zones(compiled).Select(z => (string)z.Value!).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_3_NoZoneTextReachesTheSqlAndEveryZoneIsARegistryOwnedBoundValue(TargetSqlDialect dialect)
    {
        string[] statements =
        [
            $"SELECT {Call} FROM dbo.Events e",
            $"SELECT {Call}, {Call} FROM dbo.Events e",
            "SELECT convert_timezone('Asia/Kolkata', 'America/New_York', convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt)) FROM dbo.Events e",
            $"SELECT cast({Call} AS date) FROM dbo.Events e GROUP BY cast({Call} AS date)"
        ];
        foreach (string sql in statements)
        {
            var compiled = Compile(dialect, sql);
            foreach (string leak in new[] { "UTC", "Europe", "Berlin", "Kolkata", "New_York", "India", "Eastern", "W. Europe" })
            {
                Assert.DoesNotContain(leak, compiled.Sql, StringComparison.Ordinal);
            }

            foreach (var zone in Zones(compiled))
            {
                Assert.Contains(zone.Marker, compiled.Sql, StringComparison.Ordinal);
                string iana = dialect == TargetSqlDialect.SqlServer ? WindowsToIana((string)zone.Value!) : (string)zone.Value!;
                Assert.True(TimeZoneRegistry.TryResolve(dialect, iana, out var registryValue));
                Assert.Same(registryValue, zone.Value);   // the registry's string, never a copy of the caller's text
            }
        }
    }

    private static string WindowsToIana(string windows) => windows switch
    {
        "UTC" => "UTC",
        "W. Europe Standard Time" => "Europe/Berlin",
        "India Standard Time" => "Asia/Kolkata",
        "Eastern Standard Time" => "America/New_York",
        _ => throw new InvalidOperationException()
    };

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T17_TheSameZoneInSelectAndGroupByIsOneMarker(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, $"SELECT cast({Call} AS date) AS d, count(*) FROM dbo.Events e GROUP BY cast({Call} AS date)");
        Assert.Equal(2, Zones(compiled).Count());   // UTC and Europe/Berlin once each: the markers of the two calls are equal
        var markers = Regex.Matches(compiled.Sql, dialect == TargetSqlDialect.SqlServer ? @"@p\d+" : @"\$\d+").Select(m => m.Value).ToList();
        foreach (var zone in Zones(compiled)) Assert.True(markers.Count(m => m == zone.Marker) >= 2);
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T17_ParameterZonesAreZoneSlotsAndAValueOfTheSameNameIsAnotherSlot(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "SELECT convert_timezone(__param_zone, __param_to, e.OccurredAt) AS t FROM dbo.Events e WHERE e.Label = __param_zone");
        var zoneSlots = compiled.Parameters.Where(p => p.ZoneArgument).ToList();
        Assert.Equal(2, zoneSlots.Count);
        Assert.All(zoneSlots, s =>
        {
            Assert.Equal(ParameterOrigin.ClientNamed, s.Origin);
            Assert.Equal(SqlParameterType.String, s.Type);
            Assert.Null(s.Value);
        });
        Assert.Equal(["to", "zone"], zoneSlots.Select(s => s.SourceName!.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());

        var valueSlots = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.ClientNamed && !p.ZoneArgument).ToList();
        Assert.Single(valueSlots);
        Assert.Equal("zone", valueSlots[0].SourceName!.ToLowerInvariant());
        Assert.DoesNotContain(valueSlots[0].Marker, zoneSlots.Select(s => s.Marker));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T17_ASecondZoneSlotOfTheSameNameIsTheSameMarker(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "SELECT convert_timezone(__param_zone, 'UTC', e.OccurredAt), convert_timezone(__param_zone, 'UTC', e.UpdatedAt) FROM dbo.Events e");
        Assert.Single(compiled.Parameters.Where(p => p.ZoneArgument));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T17_TheUserTimestampIsAlwaysParenthesized(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(e.Label AS timestamp)) FROM dbo.Events e");
        Assert.Matches(dialect == TargetSqlDialect.DuckDb ? @"TIMEZONE\(\$\d+, TIMEZONE\(\$\d+, CAST" : @"\(CAST\(", compiled.Sql);
    }

    // ---- BC-130-3 / BC-130-11 architecture ----

    private static string GeneratorSource(string file) => File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TrinoSqlEngine", "Ast", "Generators", file));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> GeneratorFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src", "TrinoSqlEngine", "Ast", "Generators"), "*.cs").Select(Path.GetFileName)!;

    [Fact]
    [Trait("Category", "Security")]
    public void BC_130_3_NoGeneratorOverrideMatchesTheFunctionNameItself()
    {
        foreach (string file in GeneratorFiles().Where(f => f != "SqlDialectGeneratorBase.TimeZone.cs"))
        {
            string source = GeneratorSource(file);
            Assert.DoesNotContain("convert_timezone", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void BC_130_3_TheInterceptionIsTheFirstStatementOfTheBaseGenerateFunctionCall()
    {
        string source = GeneratorSource("SqlDialectGeneratorBase.Functions.cs");
        int start = source.IndexOf("protected virtual void GenerateFunctionCall(", StringComparison.Ordinal);
        Assert.True(start > 0);
        string body = source[source.IndexOf('{', start)..];
        string firstStatement = body[1..].TrimStart();
        Assert.StartsWith("if (TimeZoneCallValidator.IsTimeZoneFunction(fn.Name))", firstStatement, StringComparison.Ordinal);
        Assert.True(body.IndexOf("IsTimeZoneFunction", StringComparison.Ordinal) < body.IndexOf("FormatFunctionName", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_13_BC_130_11_TheConversionTextIsEmittedOnlyByFormatConvertTimeZone()
    {
        string[] needles = ["AT TIME ZONE", "TIMEZONE("];
        foreach (string file in GeneratorFiles())
        {
            // comment lines may describe the emitted text; only code counts
            string source = string.Join('\n', GeneratorSource(file).Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (string needle in needles)
            {
                for (int at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + 1, StringComparison.Ordinal))
                {
                    int declaration = source.LastIndexOf("void FormatConvertTimeZone(", at, StringComparison.Ordinal);
                    Assert.True(declaration >= 0, $"{needle} outside FormatConvertTimeZone in {file}");
                    int end = source.IndexOf("\n    }\n", declaration, StringComparison.Ordinal);
                    Assert.True(at < end, $"{needle} after the end of FormatConvertTimeZone in {file}");
                }
            }
        }
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_13_NoStatementWithoutTheFunctionEmitsTheConversionText(TargetSqlDialect dialect)
    {
        string[] statements =
        [
            "SELECT e.OccurredAt, e.Label FROM dbo.Events e",
            "SELECT cast(e.OccurredAt AS date), count(*) FROM dbo.Events e GROUP BY cast(e.OccurredAt AS date)",
            "SELECT extract(year FROM e.OccurredAt) FROM dbo.Events e",
            "SELECT current_timestamp FROM dbo.Events e",
            "SELECT e.Label FROM dbo.Events e WHERE e.OccurredAt > TIMESTAMP '2026-01-01 00:00:00'",
            "SELECT coalesce(e.Label, 'x'), lower(e.Label) FROM dbo.Events e ORDER BY e.OccurredAt",
            "SELECT row_number() OVER (PARTITION BY e.Label ORDER BY e.OccurredAt) FROM dbo.Events e"
        ];
        foreach (string sql in statements)
        {
            string text = Compile(dialect, sql).Sql;
            Assert.DoesNotContain("AT TIME ZONE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TIMEZONE(", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- T-21 / ST-130-10 guards ----

    private static SelectStatement Handbuilt(Func<IReadOnlyList<Expression>, IReadOnlyList<Expression>>? arguments = null)
    {
        var select = (SelectStatement)CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e");
        if (arguments is null) return select;
        var spec = (QuerySpecification)select.Body;
        var item = (ColumnSelectItem)spec.Projections[0];
        var call = (FunctionCallExpression)item.Expression;
        return select with { Body = spec with { Projections = [item with { Expression = call with { Arguments = arguments(call.Arguments) } }] } };
    }

    [Theory]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void T21_UnsupportedBoundGeneratorsThrowTheUnsupportedCodeEvenForABypassedValidator(TargetSqlDialect dialect)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var ex = Assert.Throws<SqlTimeZoneException>(() => generator.Generate(Handbuilt(), ParameterSource.Empty, CancellationToken.None));
        Assert.Equal(GovernedSqlErrorCodes.FunctionUnsupportedDialect, ex.Code);
    }

    [Theory]
    [InlineData(TargetSqlDialect.Ansi)]
    [InlineData(TargetSqlDialect.Snowflake)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void T21_UnboundStringGenerationNeverPassesTheNameThrough(TargetSqlDialect dialect)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var ex = Assert.Throws<SqlTimeZoneException>(() => generator.GenerateSql(Handbuilt()));
        Assert.Equal(GovernedSqlErrorCodes.FunctionUnsupportedDialect, ex.Code);
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T21_TheLegacyUnboundPathHasNoBoundForm(TargetSqlDialect dialect)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        Assert.Throws<SqlCompileNotSupportedException>(() => generator.GenerateSql(Handbuilt()));
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_10_AnUnvalidatedStatementIsNeverEmitted(TargetSqlDialect dialect)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var ex = Assert.Throws<SqlTimeZoneException>(() => generator.Generate(Handbuilt(), ParameterSource.Empty, CancellationToken.None));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, ex.Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_14_TheFederatedProofIsBoundToTheValidatedCallCount()
    {
        var one = CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(e.OccurredAt AS timestamp)) FROM dbo.Events e");
        var two = CompilerTestHelpers.Build(
            "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(e.OccurredAt AS timestamp)), convert_timezone('UTC', 'Europe/Berlin', CAST(e.UpdatedAt AS timestamp)) FROM dbo.Events e");
        var none = CompilerTestHelpers.Build("SELECT e.Id FROM dbo.Events e");

        var proof = TimeZoneConversion.ValidateFederated(one, duckDbAvailable: true);
        Assert.NotNull(TimeZoneConversion.GenerateFederated(one, proof));

        // a statement with another number of calls than the validated one (a call added after validation) is refused, nothing is emitted
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversion.GenerateFederated(two, proof)).Code);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversion.GenerateFederated(one, TimeZoneConversion.ValidateFederated(none, true))).Code);
        Assert.NotNull(TimeZoneConversion.GenerateFederated(none, TimeZoneConversion.ValidateFederated(none, true)));
    }

    private static CompiledSql GenerateValidated(TargetSqlDialect dialect, SqlStatement statement) =>
        ((SqlDialectGeneratorBase)SqlDialectGeneratorFactory.GetGenerator(dialect))
            .Generate(statement, ParameterSource.Empty, DialectCapabilityTable.Default.Get(dialect), CancellationToken.None, timeZoneCallsValidated: true);

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_10_AHandBuiltAstWithAColumnZoneOrAnUnknownLiteralIsNeverEmittedEvenWhenFlaggedValidated(TargetSqlDialect dialect)
    {
        Assert.NotNull(GenerateValidated(dialect, Handbuilt()));

        var columnZone = Handbuilt(a => [new ColumnReference(new SqlQualifiedName("e", "Label")), a[1], a[2]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, columnZone)).Code);

        var concatenated = Handbuilt(a => [new BinaryExpression(a[0], BinaryOperator.Concat, a[1]), a[1], a[2]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, concatenated)).Code);

        var unknown = Handbuilt(a => [new LiteralExpression("Europe/Evil", LiteralType.String), a[1], a[2]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, unknown)).Code);

        var nullZone = Handbuilt(a => [new LiteralExpression(null, LiteralType.Null), a[1], a[2]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, nullZone)).Code);

        var positional = Handbuilt(a => [new ParameterReference("p", PositionalIndex: 1), a[1], a[2]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, positional)).Code);

        var wrongArity = Handbuilt(a => [a[0], a[1]]);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => GenerateValidated(dialect, wrongArity)).Code);
    }

    [Theory, MemberData(nameof(TimeZoneTestSupport.DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T21_AQuotedOrQualifiedSpellingIsStillInterceptedByTheGenerator(TargetSqlDialect dialect)
    {
        // BC-130-3: even a spelling that bypassed the validator never becomes a verbatim pass-through call of that name.
        foreach (var name in new[]
                 {
                     new SqlQualifiedName([new SqlIdentifier("CONVERT_TIMEZONE", true)]),
                     new SqlQualifiedName("dbo", "convert_timezone"),
                     new SqlQualifiedName("Convert_TimeZone")
                 })
        {
            var statement = (SqlStatement)new FunctionNameReplacer(name).Visit(Handbuilt());
            var failure = Record.Exception(() => SqlDialectGeneratorFactory.GetGenerator(dialect).Generate(statement, ParameterSource.Empty, CancellationToken.None));
            Assert.IsType<SqlTimeZoneException>(failure);
        }
    }
}
