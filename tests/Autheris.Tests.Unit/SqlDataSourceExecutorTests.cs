using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class SqlDataSourceExecutorTests
{
    private static TableMetadata CreateMetadata()
    {
        var id = new TableIdentifier("corp", "hr", "employees");
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "hr_db",
                SchemaName = "hr",
                TableName = "employees",
                DataSourceType = DataSourceType.Sql,
                SourceType = "PostgreSQL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "salary", DataType = "decimal" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" }
            ]
        };
    }

    private static DataSourceExecutionContext CreateContext(
        TableMetadata metadata,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess, null, hasUnconstrainedColumnAllow: false);

        return new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: arguments,
            RequestedFields: ["id", "name"]
        );
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenFilteringOnDeniedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["salary"] = 100000m
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("salary");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenFilteringOnMaskedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        };

        var arguments = new Dictionary<string, object?>
        {
            ["ssn"] = "123-45-6789"
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("ssn");
    }

    [Fact]
    public async Task ExecuteAsync_Succeeds_WhenFilteringOnlyOnClearColumns()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["id"] = 42
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var rows = await executor.ExecuteAsync(context);
        rows.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenRealSqlConnectionConfigured_AndFilteringOnDeniedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["salary"] = 50000m
        };

        var context = CreateContext(metadata, columnAccess, arguments);

        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["hr_db"] = new DataSourceConnectionOptions
                    {
                        ConnectionString = "Host=localhost;Database=hr"
                    }
                }
            }
        });

        var executor = new SqlDataSourceExecutor(connFactory, options, NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("salary");

        // Verify that no database connection was ever opened due to early security rejection
        await connFactory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Theory]
    [InlineData("geom", "geometry", DatabaseDialect.PostgreSql, "ST_AsGeoJSON(\"geom\") AS \"geom\"")]
    [InlineData("location", "geography", DatabaseDialect.SqlServer, "([location].STAsText()) AS [location]")]
    [InlineData("coords", "point", DatabaseDialect.Sqlite, "AsGeoJSON(\"coords\") AS \"coords\"")]
    public void BuildColumnProjection_GeospatialTypes_TranslatesCorrectly(string col, string type, DatabaseDialect dialect, string expected)
    {
        var projection = SqlDataSourceExecutor.BuildColumnProjection(col, type, dialect);
        projection.ShouldBe(expected);
    }

    [Theory]
    [InlineData("payload", "bytea", DatabaseDialect.PostgreSql, "encode(\"payload\", 'base64') AS \"payload\"")]
    [InlineData("bin_data", "blob", DatabaseDialect.Sqlite, "hex(\"bin_data\") AS \"bin_data\"")]
    [InlineData("raw_bytes", "varbinary", DatabaseDialect.SqlServer, "[raw_bytes]")]
    public void BuildColumnProjection_BinaryTypes_TranslatesCorrectly(string col, string type, DatabaseDialect dialect, string expected)
    {
        var projection = SqlDataSourceExecutor.BuildColumnProjection(col, type, dialect);
        projection.ShouldBe(expected);
    }

    [Theory]
    [InlineData("created_at", "timestamptz", DatabaseDialect.PostgreSql, "to_char(\"created_at\", 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') AS \"created_at\"")]
    [InlineData("updated_at", "datetimeoffset", DatabaseDialect.SqlServer, "CONVERT(VARCHAR(33), [updated_at], 126) AS [updated_at]")]
    [InlineData("recorded_at", "datetime2", DatabaseDialect.SqlServer, "CONVERT(VARCHAR(33), [recorded_at], 126) AS [recorded_at]")]
    public void BuildColumnProjection_TimestampTypes_TranslatesCorrectly(string col, string type, DatabaseDialect dialect, string expected)
    {
        var projection = SqlDataSourceExecutor.BuildColumnProjection(col, type, dialect);
        projection.ShouldBe(expected);
    }

    [Theory]
    [InlineData("col; DROP TABLE users; --")]
    [InlineData("col\"")]
    [InlineData("col name with spaces")]
    [InlineData("1invalid_start")]
    public void BuildColumnProjection_RejectsSqlInjectionInColumnName(string maliciousCol)
    {
        Should.Throw<ArgumentException>(() =>
            SqlDataSourceExecutor.BuildColumnProjection(maliciousCol, "varchar", DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void NormalizeReadValue_BinaryWithinLimit_ConvertsToBase64()
    {
        var bytes = new byte[] { 0x47, 0x51, 0x4C, 0x31 };
        var normalized = SqlDataSourceExecutor.NormalizeReadValue(bytes, "data");

        normalized.ShouldBe(Convert.ToBase64String(bytes));
    }

    [Fact]
    public void NormalizeReadValue_BinaryExceeding16MB_ThrowsSecurityException()
    {
        var oversized = new byte[SqlDataSourceExecutor.MaxAllowedBinaryBytes + 1];

        var ex = Should.Throw<SecurityException>(() =>
            SqlDataSourceExecutor.NormalizeReadValue(oversized, "huge_blob"));

        ex.Message.ShouldContain("überschreitet die zulässige Maximalgröße");
        ex.Message.ShouldContain("huge_blob");
    }

    [Fact]
    public void NormalizeReadValue_DateTimeAndDateTimeOffset_NormalizesToUtcIso8601()
    {
        var dt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var dto = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.FromHours(2));

        var normDt = SqlDataSourceExecutor.NormalizeReadValue(dt, "dt_col");
        var normDto = SqlDataSourceExecutor.NormalizeReadValue(dto, "dto_col");

        normDt.ShouldBe("2026-09-30T12:00:00.0000000Z");
        normDto.ShouldBe("2026-09-30T12:00:00.0000000Z");
    }

    [Fact]
    public void BuildColumnProjection_MssqlRowversionAndTimestamp_TreatedAsBinaryNotDatetime()
    {
        // SEC-CRIT: In MSSQL, timestamp is a synonym for rowversion (8-byte binary token). It must NOT be converted using datetime styles!
        var projTs = SqlDataSourceExecutor.BuildColumnProjection("row_version", "timestamp", DatabaseDialect.SqlServer);
        var projRv = SqlDataSourceExecutor.BuildColumnProjection("version", "rowversion", DatabaseDialect.SqlServer);

        projTs.ShouldBe("[row_version]");
        projRv.ShouldBe("[version]");
    }

    [Theory]
    [InlineData("shape", "sdo_geometry", DatabaseDialect.Oracle, "SDO_UTIL.TO_GEOJSON(\"shape\") AS \"shape\"")]
    [InlineData("raw_bytes", "raw", DatabaseDialect.Oracle, "RAWTOHEX(\"raw_bytes\") AS \"raw_bytes\"")]
    [InlineData("created_at", "timestamp", DatabaseDialect.Oracle, "TO_CHAR(\"created_at\", 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6\"Z\"') AS \"created_at\"")]
    [InlineData("hire_date", "date", DatabaseDialect.Oracle, "TO_CHAR(\"hire_date\", 'YYYY-MM-DD') AS \"hire_date\"")]
    public void BuildColumnProjection_OracleTypes_TranslatesCorrectly(string col, string type, DatabaseDialect dialect, string expected)
    {
        var projection = SqlDataSourceExecutor.BuildColumnProjection(col, type, dialect);
        projection.ShouldBe(expected);
    }

    [Theory]
    [InlineData("bin_data", "binary", DatabaseDialect.Databricks, "base64(`bin_data`) AS `bin_data`")]
    [InlineData("event_time", "timestamp", DatabaseDialect.Databricks, "date_format(`event_time`, 'yyyy-MM-dd''T''HH:mm:ss.SSS''Z''') AS `event_time`")]
    [InlineData("log_date", "date", DatabaseDialect.Databricks, "date_format(`log_date`, 'yyyy-MM-dd') AS `log_date`")]
    public void BuildColumnProjection_DatabricksTypes_TranslatesCorrectly(string col, string type, DatabaseDialect dialect, string expected)
    {
        var projection = SqlDataSourceExecutor.BuildColumnProjection(col, type, dialect);
        projection.ShouldBe(expected);
    }

    [Fact]
    public void NormalizeReadValue_DateOnlyTimeOnlyAndIsoStrings_NormalizesCleanly()
    {
        var date = new DateOnly(2026, 9, 30);
        var time = new TimeOnly(14, 30, 15);
        var span = new TimeSpan(1, 2, 3);
        var rawIsoNoZ = "2026-09-30T14:30:15.1234567";

        var normDate = SqlDataSourceExecutor.NormalizeReadValue(date, "d");
        var normTime = SqlDataSourceExecutor.NormalizeReadValue(time, "t");
        var normSpan = SqlDataSourceExecutor.NormalizeReadValue(span, "ts");
        var normIso = SqlDataSourceExecutor.NormalizeReadValue(rawIsoNoZ, "iso");

        normDate.ShouldBe("2026-09-30");
        normTime.ShouldBe("14:30:15.0000000");
        normSpan.ShouldBe("01:02:03");
        normIso.ShouldBe("2026-09-30T14:30:15.1234567Z");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenTableAccessDecisionIsDenied()
    {
        var metadata = CreateMetadata();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var deniedDecision = TableAccessDecision.Denied(metadata.Identifier, "Casbin ABAC Policy Deny");

        var context = new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: deniedDecision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "name"]
        );

        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("Casbin ABAC Policy Deny");
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesDeniedColumns_FromSyntheticMockGeneration()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny,
            ["ssn"] = ColumnAccessLevel.Deny
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess);

        var context = new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "name", "salary", "ssn"] // Request all columns including denied ones
        );

        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);
        var rows = await executor.ExecuteAsync(context);

        rows.ShouldNotBeEmpty();
        foreach (var row in rows)
        {
            row.ContainsKey("id").ShouldBeTrue();
            row.ContainsKey("name").ShouldBeTrue();
            row.ContainsKey("salary").ShouldBeFalse();
            row.ContainsKey("ssn").ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("tenant_id = 'T1' --")]
    [InlineData("tenant_id = 'T1'; DROP TABLE users;")]
    [InlineData("tenant_id = 'T1' /* comment */")]
    public async Task ExecuteAsync_ThrowsArgumentException_WhenCombinedRowFilterSqlHasCommentInjection(string maliciousRls)
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess, rowFilterSql: maliciousRls);

        var context = new DataSourceExecutionContext(
            SourceName: "hr_db",
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id"]
        );

        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["hr_db"] = new DataSourceConnectionOptions
                    {
                        ConnectionString = "Host=localhost;Database=hr",
                        Provider = "PostgreSQL"
                    }
                }
            }
        });

        var executor = new SqlDataSourceExecutor(connFactory, options, NullLogger<SqlDataSourceExecutor>.Instance);

        await Should.ThrowAsync<ArgumentException>(() => executor.ExecuteAsync(context));
    }
}
