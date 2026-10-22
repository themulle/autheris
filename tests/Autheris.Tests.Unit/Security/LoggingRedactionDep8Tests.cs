namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Domain.Diagnostics;
using Autheris.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

/// <summary>
/// DEP-8: OpenTelemetry logging replaces Serilog. Log redaction processor enforces query string stripping,
/// sensitive keyword masking, and exception message redaction.
/// </summary>
public sealed class LoggingRedactionDep8Tests
{
    private sealed record LogRecordSnapshot(
        LogLevel LogLevel,
        string? Body,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>>? Attributes);

    private sealed class TestLogRecordProcessor(List<LogRecordSnapshot> records) : OpenTelemetry.BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record)
        {
            records.Add(new LogRecordSnapshot(
                record.LogLevel,
                record.FormattedMessage ?? record.Body,
                record.Exception,
                record.Attributes?.ToList()));
        }
    }

    private static (ILogger Logger, List<LogRecordSnapshot> Records) CreateTestLogger(bool includeTenant = false)
    {
        var records = new List<LogRecordSnapshot>();
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.AddProcessor(new LogRedactionProcessor(includeTenant));
                logging.AddProcessor(new TestLogRecordProcessor(records));
            });
        });

        return (factory.CreateLogger("TestLogger"), records);
    }

    [Fact]
    public void QueryStringAndUrlProperties_AreRedacted()
    {
        var (logger, records) = CreateTestLogger(includeTenant: false);
        logger.LogWarning("Request {QueryString} {Uri}", "?$filter=email eq 'alice@corp.example'", "https://gw/odata/x?$filter=ssn eq '123'");

        records.Count.ShouldBe(1);
        var record = records[0];
        record.Attributes.ShouldNotBeNull();

        var qsAttr = record.Attributes.First(a => a.Key == "QueryString").Value?.ToString();
        var uriAttr = record.Attributes.First(a => a.Key == "Uri").Value?.ToString();

        qsAttr.ShouldBe(string.Empty);
        uriAttr.ShouldBe("https://gw/odata/x");
    }

    [Fact]
    public void SensitiveKeywords_AreRedacted()
    {
        var (logger, records) = CreateTestLogger(includeTenant: false);
        logger.LogInformation(
            "Processing query: {SqlQuery}, {Password}, {UserToken}, {DataSource}, {ErrorCode}",
            "SELECT * FROM Users WHERE Secret = '123'",
            "SuperSecret123",
            "jwt-token-value",
            "orders_db",
            "NOT_FOUND");

        records.Count.ShouldBe(1);
        var record = records[0];
        record.Attributes.ShouldNotBeNull();

        record.Attributes.First(a => a.Key == "SqlQuery").Value.ShouldBe("[redacted]");
        record.Attributes.First(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.Attributes.First(a => a.Key == "UserToken").Value.ShouldBe("[redacted]");
        record.Attributes.First(a => a.Key == "DataSource").Value.ShouldBe("orders_db");
        record.Attributes.First(a => a.Key == "ErrorCode").Value.ShouldBe("NOT_FOUND");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void INF_12_MessageIpPrincipalAndKeyAttributes_AreRedacted()
    {
        var (logger, records) = CreateTestLogger(includeTenant: false);
        logger.LogWarning(
            "Failed {Message} {ErrorMessage} {Ip} {Principal} {IdempotencyKey} {DataSource}",
            "Key (email)=(alice@x) already exists",
            "driver detail",
            "10.1.2.3",
            "Alice Example",
            "idem-123",
            "orders_db");

        var record = records[0];
        record.Attributes.ShouldNotBeNull();
        foreach (var key in new[] { "Message", "ErrorMessage", "Ip", "Principal", "IdempotencyKey" })
        {
            record.Attributes.First(a => a.Key == key).Value.ShouldBe("[redacted]");
        }

        record.Attributes.First(a => a.Key == "DataSource").Value.ShouldBe("orders_db");
    }

    [Fact]
    public void Exceptions_AreWrappedInRedactedException()
    {
        var (logger, records) = CreateTestLogger(includeTenant: false);
        var dbEx = new InvalidOperationException("Fatal SQL syntax error near 'SELECT * FROM Passwords'");

        logger.LogError(dbEx, "Database failure");

        records.Count.ShouldBe(1);
        var record = records[0];
        record.Exception.ShouldNotBeNull();
        record.Exception.ShouldBeOfType<RedactedException>();
        record.Exception.Message.ShouldBe(TelemetryExceptionPolicy.RedactedPlaceholder);
        ((RedactedException)record.Exception).OriginalTypeName.ShouldBe(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void TenantContext_IsEnriched_WhenConfigured()
    {
        using (TelemetryTenantContext.Enter("tenant-corp-123"))
        {
            var (logger, records) = CreateTestLogger(includeTenant: true);
            logger.LogInformation("Tenant action");

            records.Count.ShouldBe(1);
            var record = records[0];
            record.Attributes.ShouldNotBeNull();
            record.Attributes.Any(a => a.Key == TelemetryAttributes.AutherisTenantId && (string?)a.Value == "tenant-corp-123").ShouldBeTrue();
        }
    }

    [Fact]
    public void AppSettings_ContainsLoggingOverridesAndNoSerilog()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "src", "Autheris.Api", "appsettings.json"));
        json.ShouldNotContain("\"Serilog\"");
        json.ShouldContain("\"Microsoft.AspNetCore\": \"Warning\"");
        json.ShouldContain("\"Gateway\": {");
        json.ShouldContain("\"Telemetry\": {");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
