namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Unit.Sql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// #333 (REQ-PGP-01..04, REQ-PGP-08): the planned time-bucket SQL of PostgreSQL and SQL Server compiles through the real AST compiler
/// (date translation and time zone conversion on, like the governed path) and the dialect generator translates it.
/// </summary>
public sealed class TimeBucketingPlannedSqlCompileTests
{
    private static readonly TableIdentifier LogsTableId = new("ops", "dbo", "logs");

    private static TableMetadata Table(string sourceType, string tsType) => new()
    {
        Identifier = LogsTableId,
        PrimaryKeyColumns = ["id"],
        Table = new Table
        {
            SourceName = "LogsSource",
            SchemaName = "dbo",
            TableName = "logs",
            SourceType = sourceType,
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "ts", DataType = tsType },
            new TableColumn { ColumnName = "d", DataType = "date" }
        ]
    };

    private static (string Provider, TargetSqlDialect Target, string TsType) Pg() => ("PostgreSQL", TargetSqlDialect.PostgreSql, "timestamp");

    private static (string Provider, TargetSqlDialect Target, string TsType) Mssql() => ("SqlServer", TargetSqlDialect.SqlServer, "datetime2");

    public static TheoryData<string, TimeBucketInterval, string> Cases()
    {
        var data = new TheoryData<string, TimeBucketInterval, string>();
        foreach (var dialect in new[] { "PostgreSQL", "SqlServer" })
        {
            foreach (var interval in Enum.GetValues<TimeBucketInterval>())
            {
                foreach (var zone in new[] { "UTC", "Europe/Berlin" })
                {
                    data.Add(dialect, interval, zone);
                }
            }
        }

        return data;
    }

    private static PlannedTableAggregate Plan(string provider, string tsType, string column, TimeBucketInterval interval, string zone)
    {
        var metadata = Table(provider, tsType);
        var decision = TableAccessDecision.Allowed(LogsTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var intent = new TableAggregateIntent(
            LogsTableId,
            GroupBy: [column],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification(column, interval, zone)]);
        return new GovernedTableReadPlanner().PlanAggregate(intent, metadata, decision);
    }

    private static CompiledSql Compile(string sql, TargetSqlDialect dialect, string tsType)
    {
        var env = new TestCompileEnv(dialect, "dbo").Table("logs", "tenant_id", "id:bigint", "tenant_id:varchar", $"ts:{tsType}", "d:date");
        var policy = env.Request().Policy;
        var allowed = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.GetDefault(dialect));
        var request = CompileRequestFactory.Create(policy, dialect, new WebSqlOptions(), false, 1000, allowed, null, true, true);
        return env.Engine.Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PlannedBucket_Compiles(string provider, TimeBucketInterval interval, string zone)
    {
        var (_, target, tsType) = provider == "SqlServer" ? Mssql() : Pg();
        var planned = Plan(provider, tsType, "ts", interval, zone);

        var compiled = Compile(planned.Sql, target, tsType);

        compiled.Sql.ShouldNotBeNullOrWhiteSpace();
        if (provider == "PostgreSQL")
        {
            compiled.Sql.ShouldContain($"DATE_TRUNC('{interval.ToString().ToLowerInvariant()}'");
        }
        else
        {
            compiled.Sql.ShouldNotContain("DATE_TRUNC", Case.Insensitive);
            compiled.Sql.ShouldContain("datetimeoffset");
        }
    }

    [Fact]
    public void PlannedBucket_DateColumn_CastsToTimestamp()
    {
        var planned = Plan("PostgreSQL", "timestamp", "d", TimeBucketInterval.Month, "UTC");

        planned.Sql.ShouldContain("date_trunc('month', CAST(\"d\" AS timestamp)) AS \"d\"");
    }

    [Theory]
    [InlineData("PostgreSQL", "timestamptz")]
    [InlineData("PostgreSQL", "timestamp with time zone")]
    [InlineData("SqlServer", "datetimeoffset")]
    [InlineData("SqlServer", "varchar")]
    [InlineData("PostgreSQL", "text")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-02")]
    public void PlannedBucket_ZonedColumn_Throws400_NoTypeLeak(string provider, string type)
    {
        var ex = Should.Throw<ArgumentException>(() => Plan(provider, type, "ts", TimeBucketInterval.Day, "UTC"));

        ex.Message.ShouldNotContain(type, Case.Insensitive);
        ex.Message.ShouldNotContain("ts", Case.Sensitive);
        AggregateFailureMapper.ForAggregate(ex)!.Errors[0].Code.ShouldBe("AGGREGATION_ARGUMENT_INVALID");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-02")]
    public void PlannedBucket_SqlServerTimestampIsRowversion_Throws400()
    {
        var ex = Should.Throw<ArgumentException>(() => Plan("SqlServer", "timestamp", "ts", TimeBucketInterval.Day, "UTC"));

        AggregateFailureMapper.ForAggregate(ex)!.Errors[0].Code.ShouldBe("AGGREGATION_ARGUMENT_INVALID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geometry")]
    [InlineData("timestamp(x)")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-02")]
    public void PlannedBucket_UnknownOrNullColumnMetadata_Throws400_FixedMessage(string? dataType)
    {
        foreach (var dialect in new[] { DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer })
        {
            var ex = Should.Throw<ArgumentException>(() => GovernedTableReadPlanner.BuildTimeBucketExpression(
                "secret_col", new TimeBucketSpecification("secret_col", TimeBucketInterval.Day, "Europe/Berlin"), dialect, dataType));

            ex.Message.ShouldNotContain("secret_col");
            ex.Message.ShouldNotContain("Berlin");
            if (dataType is { Length: > 2 })
            {
                ex.Message.ShouldNotContain(dataType);
            }
        }
    }

    [Fact]
    public void PlannedBucket_UnknownZone_ThrowsArgument() =>
        Should.Throw<ArgumentException>(() => Plan("PostgreSQL", "timestamp", "ts", TimeBucketInterval.Day, "Mars/Olympus"));

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-01")]
    public void PlannedBucket_UnitText_ComesOnlyFromEnum()
    {
        foreach (var interval in Enum.GetValues<TimeBucketInterval>())
        {
            var planned = Plan("PostgreSQL", "timestamp", "ts", interval, "UTC");
            Regex.IsMatch(planned.Sql, @"date_trunc\('(hour|day|week|month|quarter|year)', ").ShouldBeTrue();
        }

        Should.Throw<ArgumentOutOfRangeException>(() => Plan("PostgreSQL", "timestamp", "ts", (TimeBucketInterval)99, "UTC"));
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-01")]
    public void PlannedBucket_ZoneLiteral_IsRegistryCanonicalKey_QuotesEscaped(string provider)
    {
        var tsType = provider == "SqlServer" ? "datetime2" : "timestamp";
        foreach (var evil in new[] { "Europe/Berlin'); DROP TABLE--", "europe/berlin", "Europe/Berlin ", "Europe/Berlin\0", new string('A', 65) })
        {
            Should.Throw<ArgumentException>(() => Plan(provider, tsType, "ts", TimeBucketInterval.Day, evil));
        }

        var planned = Plan(provider, tsType, "ts", TimeBucketInterval.Day, "Europe/Berlin");
        planned.Sql.ShouldContain("convert_timezone('UTC', 'Europe/Berlin', \"ts\")");
        planned.Sql.ShouldNotContain("AT TIME ZONE", Case.Insensitive);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-04")]
    public void BucketedAggregation_TenantRowFilter_StillApplied_PgSqlServer(string provider)
    {
        var (_, target, tsType) = provider == "SqlServer" ? Mssql() : Pg();
        var planned = Plan(provider, tsType, "ts", TimeBucketInterval.Day, "Europe/Berlin");

        var compiled = Compile(planned.Sql, target, tsType);

        compiled.Sql.ShouldContain("tenant_id", Case.Insensitive);
        compiled.Parameters.Length.ShouldBeGreaterThan(0);
        compiled.Parameters.ToArray().ShouldContain(p => Equals(p.Value, "t1"));
    }

    [Fact]
    public void PlannedTimeBucketKey_IsInvariantIsoText()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            GovernedAggregationService.NormalizePlannedBucketKey(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)).ShouldBe("2023-01-01T00:00:00");
            GovernedAggregationService.NormalizePlannedBucketKey(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe("2023-01-01T00:00:00");
            GovernedAggregationService.NormalizePlannedBucketKey(new DateOnly(2023, 1, 1)).ShouldBe("2023-01-01T00:00:00");
            GovernedAggregationService.NormalizePlannedBucketKey(null).ShouldBeNull();
            GovernedAggregationService.NormalizePlannedBucketKey("2023-01-01 00:00:00").ShouldBe("2023-01-01 00:00:00");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-PGP-06")]
    public void PlannedTimeBucketKey_InvariantUnderTrTrAndDeDe_NoLogLeak(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local })
            {
                GovernedAggregationService.NormalizePlannedBucketKey(new DateTime(2023, 12, 31, 23, 0, 0, kind)).ShouldBe("2023-12-31T23:00:00");
            }

            GovernedAggregationService.NormalizePlannedBucketKey(new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.FromHours(2))).ShouldBe("2023-06-01T00:00:00");
            GovernedAggregationService.NormalizePlannedBucketKey(DateTime.MaxValue).ShouldBe("9999-12-31T23:59:59");
            GovernedAggregationService.NormalizePlannedBucketKey(DateTime.MinValue).ShouldBe("0001-01-01T00:00:00");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    public sealed class FailureMapperTests
    {
        private const string Sentinel = "SENTINEL-SELECT-secret-FROM-t";

        [Fact]
        [Trait("Category", "Security")]
        [Trait("SecurityCondition", "SEC-PGP-03")]
        public void AggregateFailureMapper_CompilerErrors_NoExceptionTextInResponse()
        {
            Exception[] errors =
            [
                new TrinoSqlEngine.Ast.Builder.AstBuildException(Sentinel, new InvalidOperationException(Sentinel)),
                new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneInvalid),
                new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument),
                new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, Sentinel)
            ];

            foreach (var error in errors)
            {
                var mapped = AggregateFailureMapper.ForAggregate(error);

                mapped.ShouldNotBeNull();
                mapped.Errors[0].Code.ShouldBe("AGGREGATION_ARGUMENT_INVALID");
                mapped.Errors[0].Message.ShouldNotContain(Sentinel);
                mapped.Errors[0].Message.ShouldNotContain("SQL_TIMEZONE");
                mapped.Errors[0].Extensions!.Keys.ShouldBe(["code"]);
                mapped.InnerException.ShouldBeNull();
            }
        }

        [Fact]
        [Trait("Category", "Security")]
        [Trait("SecurityCondition", "SEC-PGP-03")]
        public void AggregateFailureMapper_TimeZoneUnavailable_IsTheOnlyUnavailableMapping()
        {
            var mapped = AggregateFailureMapper.ForAggregate(new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneUnavailable));

            mapped!.Errors[0].Code.ShouldBe("UNAVAILABLE");
            mapped.Errors[0].Message.ShouldBe("The database is currently unavailable.");
        }
    }
}
