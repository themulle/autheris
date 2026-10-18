namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// REQ-SQL-130 (T-30..T-37, AC-130-1, AC-130-2): <c>convert_timezone</c> executed on a real SQL Server and PostgreSQL (shared Testcontainers or the
/// AUTHERIS_TEST_* servers) and on in-process DuckDB. Without Docker the container tests report as unavailable and return early like the other
/// execution tests (a run that requires its databases fails the startup instead, see <c>ContainerStartup</c>).
/// </summary>
public sealed class ConvertTimeZoneFixture : IAsyncLifetime, IDisposable
{
    private readonly DuckDBConnection _duck = new("DataSource=:memory:");

    public PostgreSqlTestDatabase? Pg { get; private set; }

    public SqlServerTestDatabase? Mssql { get; private set; }

    public static readonly (int Id, string Ts)[] Rows =
    [
        // T-31 winter and summer, UTC source and unambiguous local sources
        (1, "2026-01-15 12:00:00"), (2, "2026-07-15 12:00:00"), (7, "2026-07-15 14:00:00"), (8, "2026-01-15 13:00:00"),
        // T-32 Berlin transitions (UTC source)
        (3, "2026-03-29 00:59:59"), (4, "2026-03-29 01:00:00"), (5, "2026-10-25 00:59:59"), (6, "2026-10-25 01:00:00"),
        // T-32 New York transitions (UTC source)
        (11, "2026-03-08 06:59:59"), (12, "2026-03-08 07:00:00"), (13, "2026-11-01 05:59:59"), (14, "2026-11-01 06:00:00"),
        // T-32 half-hour zones (Kolkata, Lord Howe across both of its shifts)
        (15, "2026-07-15 12:00:00"), (16, "2026-04-04 14:59:59"), (17, "2026-04-04 15:00:00"), (18, "2026-10-03 15:29:59"), (19, "2026-10-03 15:30:00"),
        // T-33 local times on transition nights (characterization)
        (31, "2026-10-25 02:30:00"), (32, "2026-03-29 02:30:00"),
        // T-30 local day assignment
        (41, "2026-03-28 23:30:00"), (42, "2026-06-30 22:30:00"), (43, "2026-10-24 22:30:00"), (44, "2026-10-25 23:30:00")
    ];

    public async Task InitializeAsync()
    {
        _duck.Open();
        await Exec(_duck, "CREATE TABLE main.events (id INTEGER PRIMARY KEY, tenantid VARCHAR NOT NULL, ts TIMESTAMP NOT NULL);" + Insert(null));

        Pg = await PostgreSqlTestDatabase.CreateAsync();
        if (Pg.IsAvailable)
        {
            await using var conn = new NpgsqlConnection(Pg.ConnectionString);
            await conn.OpenAsync();
            await Exec(conn, "CREATE TABLE public.events (id integer PRIMARY KEY, tenantid text NOT NULL, ts timestamp NOT NULL);" + Insert(null));
        }

        Mssql = await SqlServerTestDatabase.CreateAsync();
        if (Mssql.IsAvailable)
        {
            await using var conn = new SqlConnection(Mssql.ConnectionString);
            await conn.OpenAsync();
            await Exec(conn, "CREATE TABLE dbo.events (id int NOT NULL PRIMARY KEY, tenantid nvarchar(64) NOT NULL, ts datetime2(3) NOT NULL);" + Insert(null));
        }
    }

    private static string Insert(string? _) =>
        "INSERT INTO events (id, tenantid, ts) VALUES " + string.Join(", ", Rows.Select(r => $"({r.Id}, 'acme', '{r.Ts}')")) + ";";

    private static async Task Exec(DbConnection connection, string sql)
    {
        // the target table name differs per provider: events resolves through the default schema
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.Replace("INSERT INTO events", connection is SqlConnection ? "INSERT INTO dbo.events" : connection is NpgsqlConnection ? "INSERT INTO public.events" : "INSERT INTO main.events", StringComparison.Ordinal);
        await cmd.ExecuteNonQueryAsync();
    }

    public DuckDBConnection Duck => _duck;

    public void Dispose() => _duck.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        if (Pg is not null) await Pg.DisposeAsync();
        if (Mssql is not null) await Mssql.DisposeAsync();
    }
}

public sealed class ConvertTimeZoneExecutionTests : IClassFixture<ConvertTimeZoneFixture>
{
    private readonly ConvertTimeZoneFixture _fx;
    private readonly FastSqlEngine _engine = new();

    public ConvertTimeZoneExecutionTests(ConvertTimeZoneFixture fixture) => _fx = fixture;

    public static IEnumerable<object[]> Engines() =>
        new[] { TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer }.Select(d => new object[] { d });

    private bool Available(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.PostgreSql => _fx.Pg?.IsAvailable == true,
        TargetSqlDialect.SqlServer => _fx.Mssql?.IsAvailable == true,
        _ => true
    };

    private sealed class NoPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;

        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private CompiledSql Compile(TargetSqlDialect dialect, string sql)
    {
        string schema = dialect switch { TargetSqlDialect.SqlServer => "dbo", TargetSqlDialect.PostgreSql => "public", _ => "main" };
        var (i, t, ts) = dialect == TargetSqlDialect.SqlServer ? ("int", "nvarchar(64)", "datetime2(3)") : dialect == TargetSqlDialect.PostgreSql ? ("integer", "text", "timestamp") : ("INTEGER", "VARCHAR", "TIMESTAMP");
        var table = new TableIdentity(schema, "events");
        var none = new NoPolicies();
        return _engine.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            TimeZoneConversionAvailable = true,
            Policy = new GovernancePolicy
            {
                RowFilters = none,
                Masks = none,
                Catalog = new InMemoryTableCatalog(
                    [new TableCatalogEntry(table, ImmutableArray.Create(new CatalogColumn("id", i), new CatalogColumn("tenantid", t), new CatalogColumn("ts", ts)), "tenantid", 1)], schema),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);
    }

    private async Task<(List<object?[]> Rows, string TypeName)?> RunAsync(TargetSqlDialect dialect, string sql, IReadOnlyDictionary<string, object?>? client = null, string? pgTimeZone = null)
    {
        if (!Available(dialect)) return null;
        var compiled = Compile(dialect, sql);
        DbConnection connection = dialect switch
        {
            TargetSqlDialect.DuckDb => _fx.Duck,
            TargetSqlDialect.PostgreSql => new NpgsqlConnection(pgTimeZone is null ? _fx.Pg!.ConnectionString : new NpgsqlConnectionStringBuilder(_fx.Pg!.ConnectionString) { Timezone = pgTimeZone }.ConnectionString),
            _ => new SqlConnection(_fx.Mssql!.ConnectionString)
        };
        try
        {
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            CompiledSqlBinders.For(dialect).Bind(cmd, compiled, client ?? new Dictionary<string, object?>());
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            string type = reader.FieldCount > 0 ? reader.GetDataTypeName(reader.FieldCount - 1) : string.Empty;
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add([.. values.Select(v => v is DBNull ? null : v)]);
            }

            return (rows, type);
        }
        finally
        {
            if (dialect != TargetSqlDialect.DuckDb) await connection.DisposeAsync();
        }
    }

    private static DateTime DayOf(object? value) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTime t => t.Date,
        _ => throw new InvalidOperationException("Unexpected date type.")
    };

    private static DateTime At(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task<Dictionary<int, DateTime>> Convert(TargetSqlDialect dialect, string from, string to, params int[] ids)
    {
        var result = await RunAsync(dialect, $"SELECT e.id, convert_timezone('{from}', '{to}', e.ts) AS t FROM events e WHERE e.id IN ({string.Join(", ", ids)}) ORDER BY e.id");
        if (result is null) return [];
        return result.Value.Rows.ToDictionary(r => System.Convert.ToInt32(r[0], CultureInfo.InvariantCulture), r => (DateTime)r[1]!);
    }

    private static void ShouldMatch(Dictionary<int, DateTime> actual, params (int Id, string Expected)[] expected)
    {
        if (actual.Count == 0) return;   // the database is not available on this machine
        foreach (var (id, text) in expected)
        {
            actual[id].ShouldBe(At(text), $"id {id}");
        }
    }

    // ---- T-31 ----

    [Theory, MemberData(nameof(Engines))]
    public async Task T31_WinterAndSummerUtcToBerlinAndBack(TargetSqlDialect dialect)
    {
        ShouldMatch(await Convert(dialect, "UTC", "Europe/Berlin", 1, 2), (1, "2026-01-15 13:00:00"), (2, "2026-07-15 14:00:00"));
        ShouldMatch(await Convert(dialect, "Europe/Berlin", "UTC", 7, 8), (7, "2026-07-15 12:00:00"), (8, "2026-01-15 12:00:00"));
    }

    // ---- T-32 ----

    [Theory, MemberData(nameof(Engines))]
    public async Task T32_TransitionDaysWithAUtcSourceAreIdenticalOnAllEngines(TargetSqlDialect dialect)
    {
        ShouldMatch(await Convert(dialect, "UTC", "Europe/Berlin", 3, 4, 5, 6),
            (3, "2026-03-29 01:59:59"), (4, "2026-03-29 03:00:00"), (5, "2026-10-25 02:59:59"), (6, "2026-10-25 02:00:00"));
        ShouldMatch(await Convert(dialect, "UTC", "America/New_York", 11, 12, 13, 14),
            (11, "2026-03-08 01:59:59"), (12, "2026-03-08 03:00:00"), (13, "2026-11-01 01:59:59"), (14, "2026-11-01 01:00:00"));
        ShouldMatch(await Convert(dialect, "UTC", "Asia/Kolkata", 15), (15, "2026-07-15 17:30:00"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task T32_LordHoweShiftsByThirtyMinutes(TargetSqlDialect dialect)
    {
        // DST ends 2026-04-05 02:00 local (+11 -> +10:30: the clock goes back 30 minutes) and starts 2026-10-04 02:00 local (+10:30 -> +11).
        ShouldMatch(await Convert(dialect, "UTC", "Australia/Lord_Howe", 16, 17, 18, 19),
            (16, "2026-04-05 01:59:59"), (17, "2026-04-05 01:30:00"), (18, "2026-10-04 01:59:59"), (19, "2026-10-04 02:30:00"));

        // cross-check with the operating system data where it has the zone (the databases decide, this only guards the expectations above)
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");
            TimeZoneInfo.ConvertTimeFromUtc(At("2026-04-04 15:00:00"), zone).ShouldBe(At("2026-04-05 01:30:00"));
            TimeZoneInfo.ConvertTimeFromUtc(At("2026-10-03 15:30:00"), zone).ShouldBe(At("2026-10-04 02:30:00"));
        }
        catch (TimeZoneNotFoundException)
        {
            // tzdata missing on this machine
        }
    }

    // ---- T-33 characterization ----

    [Theory, MemberData(nameof(Engines))]
    public async Task T33_LocalTimesOnTransitionNightsNeverThrowAndResolveToAValidInstant(TargetSqlDialect dialect)
    {
        var converted = await Convert(dialect, "Europe/Berlin", "UTC", 31, 32);
        if (converted.Count == 0) return;

        // ambiguous 2026-10-25 02:30 Berlin is 00:30Z (CEST) or 01:30Z (CET); non-existent 2026-03-29 02:30 Berlin maps next to 00:30Z/01:30Z
        converted[31].ShouldBeOneOf(At("2026-10-25 00:30:00"), At("2026-10-25 01:30:00"));
        converted[32].ShouldBeOneOf(At("2026-03-29 00:30:00"), At("2026-03-29 01:30:00"));
        // Observed per engine (A-12 documents it): the repeated hour resolves to the second occurrence (CET) on PostgreSQL and DuckDB and to the first (CEST)
        // on SQL Server; the skipped hour is read with the offset before the transition (+01:00) everywhere. Pinned, so a changed engine rule is noticed.
        converted[31].ShouldBe(dialect == TargetSqlDialect.SqlServer ? At("2026-10-25 00:30:00") : At("2026-10-25 01:30:00"));
        converted[32].ShouldBe(At("2026-03-29 01:30:00"));
    }

    // ---- T-30 GROUP BY ----

    [Theory, MemberData(nameof(Engines))]
    public async Task T30_GroupByTheConvertedLocalDayAssignsTheInstantsToLocalDays(TargetSqlDialect dialect)
    {
        const string call = "cast(convert_timezone('UTC', 'Europe/Berlin', e.ts) as date)";
        var result = await RunAsync(dialect, $"SELECT {call} AS d, count(*) AS n FROM events e WHERE e.id IN (41, 42, 43, 44) GROUP BY {call} ORDER BY d");
        if (result is null) return;

        result.Value.Rows.Select(r => DayOf(r[0])).ToArray().ShouldBe([new DateTime(2026, 3, 29), new DateTime(2026, 7, 1), new DateTime(2026, 10, 25), new DateTime(2026, 10, 26)]);
        result.Value.Rows.ShouldAllBe(r => System.Convert.ToInt64(r[1], CultureInfo.InvariantCulture) == 1);
    }

    // ---- parameter zones through the binder ----

    [Theory, MemberData(nameof(Engines))]
    public async Task T19_ParameterZonesAreMappedByTheBinderAndConvertOnTheRealEngine(TargetSqlDialect dialect)
    {
        var result = await RunAsync(dialect, "SELECT e.id, convert_timezone(__param_from, __param_to, e.ts) AS t FROM events e WHERE e.id IN (1, 2) ORDER BY e.id",
            new Dictionary<string, object?> { ["from"] = "UTC", ["to"] = "Europe/Berlin" });
        if (result is null) return;

        result.Value.Rows.Select(r => (DateTime)r[1]!).ToArray().ShouldBe([At("2026-01-15 13:00:00"), At("2026-07-15 14:00:00")]);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task T19_ANestedConversionChainsTheZones(TargetSqlDialect dialect)
    {
        var result = await RunAsync(dialect, "SELECT convert_timezone('Europe/Berlin', 'America/New_York', convert_timezone('UTC', 'Europe/Berlin', e.ts)) AS t FROM events e WHERE e.id = 2");
        if (result is null) return;

        result.Value.Rows.Single()[0].ShouldBe(At("2026-07-15 08:00:00"));   // 12:00Z is 08:00 in New York in July
    }

    // ---- ST-130-1 / ST-130-2 on the real engines ----

    [Theory, MemberData(nameof(Engines))]
    [Trait("Category", "Security")]
    public async Task ST_130_2_AHostileZoneParameterNeverReachesTheEngine_AndTheTableIsUntouched(TargetSqlDialect dialect)
    {
        foreach (object? hostile in new object?[] { "Europe/Berlin'; DROP TABLE events;--", "x' OR 1=1--", "europe/berlin", new string('a', 65), null, 42 })
        {
            await Should.ThrowAsync<SqlTimeZoneException>(() => RunAsync(dialect, "SELECT convert_timezone(__param_from, 'UTC', e.ts) AS t FROM events e",
                new Dictionary<string, object?> { ["from"] = hostile }));
        }

        var count = await RunAsync(dialect, "SELECT count(*) AS n FROM events e");
        if (count is null) return;
        System.Convert.ToInt64(count.Value.Rows.Single()[0], CultureInfo.InvariantCulture).ShouldBe(ConvertTimeZoneFixture.Rows.Length);
    }

    [Theory, MemberData(nameof(Engines))]
    [Trait("Category", "Security")]
    public void ST_130_1_AHostileZoneLiteralIsRefusedAtCompileTimeAndNothingIsEmitted(TargetSqlDialect dialect)
    {
        foreach (string zone in new[] { "Europe/Berlin''; DROP TABLE events", "Europe/Evil", "EST", " UTC", "utc" })
        {
            var ex = Should.Throw<SqlTimeZoneException>(() => Compile(dialect, $"SELECT convert_timezone('{zone}', 'UTC', e.ts) AS t FROM events e"));
            ex.Code.ShouldBe(GovernedSqlErrorCodes.TimeZoneInvalid);
        }
    }

    // ---- T-35 result type ----

    [Fact]
    public async Task T35_DuckDbReturnsAPlainTimestamp()
    {
        var result = await RunAsync(TargetSqlDialect.DuckDb, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.ts) AS t FROM events e WHERE e.id = 1");
        result!.Value.TypeName.ToUpperInvariant().ShouldBe("TIMESTAMP");
    }

    [Fact]
    public async Task T35_PostgreSqlReturnsATimestampWithoutOffsetRegardlessOfTheSessionTimeZone()
    {
        const string sql = "SELECT convert_timezone('UTC', 'Europe/Berlin', e.ts) AS t FROM events e WHERE e.id = 2";
        var utc = await RunAsync(TargetSqlDialect.PostgreSql, sql, pgTimeZone: "UTC");
        var auckland = await RunAsync(TargetSqlDialect.PostgreSql, sql, pgTimeZone: "Pacific/Auckland");
        if (utc is null || auckland is null) return;

        utc.Value.TypeName.ShouldBe("timestamp without time zone");
        auckland.Value.TypeName.ShouldBe("timestamp without time zone");
        utc.Value.Rows.Single()[0].ShouldBe(At("2026-07-15 14:00:00"));
        auckland.Value.Rows.Single()[0].ShouldBe(At("2026-07-15 14:00:00"));
    }

    [Fact]
    public async Task T35_SqlServerReturnsDatetime2NeverADatetimeoffset()
    {
        var result = await RunAsync(TargetSqlDialect.SqlServer, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.ts) AS t FROM events e WHERE e.id = 1");
        if (result is null) return;

        result.Value.TypeName.ShouldBe("datetime2");
        result.Value.Rows.Single()[0].ShouldBeOfType<DateTime>();
    }

    // ---- T-37 ----

    [Theory]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    public async Task T37_DateTruncOfTheConvertedValueGivesTheLocalDay(TargetSqlDialect dialect)
    {
        var result = await RunAsync(dialect, "SELECT date_trunc('day', convert_timezone('UTC', 'Europe/Berlin', e.ts)) AS d FROM events e WHERE e.id = 41");
        if (result is null) return;

        DayOf(result.Value.Rows.Single()[0]).ShouldBe(new DateTime(2026, 3, 29));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task T37_CastToDateOfTheConvertedValueGivesTheLocalDay(TargetSqlDialect dialect)
    {
        var result = await RunAsync(dialect, "SELECT cast(convert_timezone('UTC', 'Europe/Berlin', e.ts) as date) AS d FROM events e WHERE e.id = 41");
        if (result is null) return;

        DayOf(result.Value.Rows.Single()[0]).ShouldBe(new DateTime(2026, 3, 29));
    }
}
