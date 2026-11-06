namespace Autheris.Tests.Unit.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autheris.Application.Observability.Telemetry;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SourceCommandTelemetryTests
{
    [Fact]
    public void Start_SuccessfulCommand_EmitsSpanWithAttributesAndEvents()
    {
        Activity? capturedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Autheris.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => capturedActivity = act
        };
        ActivitySource.AddActivityListener(listener);

        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.Detailed,
            StatementText: StatementTextMode.Parameterized,
            SlowQueryThreshold: TimeSpan.FromSeconds(10),
            RecordRowCounts: true);

        var provider = Substitute.For<IDataSourceTelemetryPolicyProvider>();
        provider.GetPolicy(Arg.Any<string>()).Returns(policy);

        var descriptor = new SourceCommandDescriptor(
            DataSourceName: "pg_sales",
            DbSystemName: "postgresql",
            Namespace: "public",
            OperationName: "SELECT",
            CollectionName: "orders",
            CompiledSql: "SELECT id, total FROM orders WHERE status = 'ACTIVE' AND id = 42;",
            Dialect: DatabaseDialect.PostgreSql);

        using (var scope = SourceCommandTelemetry.Start(descriptor, provider))
        {
            scope.RecordFirstRow();
            scope.SetRows(15);
            scope.SetBytes(4096);
            scope.RecordStreamEnd();
        }

        capturedActivity.ShouldNotBeNull();
        capturedActivity.DisplayName.ShouldBe("SELECT orders");
        capturedActivity.GetTagItem(TelemetryAttributes.DbSystemName).ShouldBe("postgresql");
        capturedActivity.GetTagItem(TelemetryAttributes.DbNamespace).ShouldBe("public");
        capturedActivity.GetTagItem(TelemetryAttributes.DbOperationName).ShouldBe("SELECT");
        capturedActivity.GetTagItem(TelemetryAttributes.DbCollectionName).ShouldBe("orders");
        capturedActivity.GetTagItem(TelemetryAttributes.AutherisDataSourceName).ShouldBe("pg_sales");
        capturedActivity.GetTagItem(TelemetryAttributes.AutherisDbRows).ShouldBe(15L);
        capturedActivity.GetTagItem(TelemetryAttributes.AutherisDbBytes).ShouldBe(4096L);

        // Scrubbed SQL parameter replacement
        var scrubbedSql = capturedActivity.GetTagItem(TelemetryAttributes.DbQueryText) as string;
        scrubbedSql.ShouldNotBeNull();
        scrubbedSql.ShouldContain("?");
        scrubbedSql.ShouldNotContain("'ACTIVE'");
        scrubbedSql.ShouldNotContain("42");

        var events = capturedActivity.Events.Select(e => e.Name).ToList();
        events.ShouldContain("autheris.db.first_row");
        events.ShouldContain("autheris.db.stream_end");
    }

    [Fact]
    public void Start_FailedCommand_RecordsErrorStatusAndTags()
    {
        Activity? capturedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Autheris.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => capturedActivity = act
        };
        ActivitySource.AddActivityListener(listener);

        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.Basic,
            StatementText: StatementTextMode.None,
            SlowQueryThreshold: TimeSpan.FromSeconds(10),
            RecordRowCounts: true);

        var provider = Substitute.For<IDataSourceTelemetryPolicyProvider>();
        provider.GetPolicy(Arg.Any<string>()).Returns(policy);

        var descriptor = new SourceCommandDescriptor(
            DataSourceName: "mssql_db",
            DbSystemName: "mssql",
            Namespace: null,
            OperationName: "EXEC",
            CollectionName: "CalculateTotals",
            CompiledSql: null,
            Dialect: DatabaseDialect.SqlServer);

        using (var scope = SourceCommandTelemetry.Start(descriptor, provider))
        {
            scope.Fail(new TimeoutException("Command timed out after 30s"));
        }

        capturedActivity.ShouldNotBeNull();
        capturedActivity.Status.ShouldBe(ActivityStatusCode.Error);
        capturedActivity.GetTagItem(TelemetryAttributes.ErrorType).ShouldBe("TimeoutException");
    }

    [Fact]
    public void Start_WhenSpanDetailIsNone_DoesNotCreateActivity()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.None,
            StatementText: StatementTextMode.None,
            SlowQueryThreshold: null,
            RecordRowCounts: false);

        var provider = Substitute.For<IDataSourceTelemetryPolicyProvider>();
        provider.GetPolicy(Arg.Any<string>()).Returns(policy);

        var descriptor = new SourceCommandDescriptor(
            DataSourceName: "mssql_db",
            DbSystemName: "mssql",
            Namespace: null,
            OperationName: "SELECT",
            CollectionName: "items",
            CompiledSql: null,
            Dialect: DatabaseDialect.SqlServer);

        // Calling Start and disposing should execute cleanly with zero activity
        using var scope = SourceCommandTelemetry.Start(descriptor, provider);
        scope.SetRows(100);
        scope.SetBytes(2000);
    }

    [Fact]
    public void ServiceInstance_StartsCommand_UsingInjectedLoggersAndPolicy()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.None,
            StatementText: StatementTextMode.None,
            SlowQueryThreshold: TimeSpan.Zero, // immediate slow query without sleeping
            RecordRowCounts: true);

        var policyProvider = Substitute.For<IDataSourceTelemetryPolicyProvider>();
        policyProvider.GetPolicy("test_source").Returns(policy);

        var mockLogger = Substitute.For<ILogger>();
        var dataLoggers = Substitute.For<IDataSourceLoggers>();
        dataLoggers.Get("test_source").Returns(mockLogger);

        ISourceCommandTelemetry service = new SourceCommandTelemetryService(policyProvider, dataLoggers);

        var descriptor = new SourceCommandDescriptor(
            DataSourceName: "test_source",
            DbSystemName: "sqlite",
            Namespace: null,
            OperationName: "SELECT",
            CollectionName: "users",
            CompiledSql: "SELECT 1;",
            Dialect: DatabaseDialect.Sqlite);

        using (var scope = service.Start(descriptor))
        {
            scope.SetRows(1);
        }

        dataLoggers.Received(1).Get("test_source");
    }

    [Fact]
    public void DataSourceLoggersService_ProducesNormalizedCategoryLoggers()
    {
        var factory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        factory.CreateLogger("Autheris.DataSource.sales_db").Returns(logger);

        var service = new DataSourceLoggersService(factory);
        var resolved = service.Get("sales_db");

        resolved.ShouldBe(logger);
        factory.Received(1).CreateLogger("Autheris.DataSource.sales_db");
    }
}
