namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

/// <summary>
/// DEP-8: Serilog replaces the Microsoft LoggerFactory. Framework request logs (full URL with $filter literals) stay
/// below the sink level, and query strings that are logged are redacted.
/// </summary>
public sealed class LoggingRedactionDep8Tests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Logger Logger, CollectingSink Sink) Create()
    {
        var sink = new CollectingSink();
        var logger = SensitiveLogPropertyEnricher.ApplyDefaults(new LoggerConfiguration()).WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void AspNetCoreRequestLogging_AtInformation_IsSuppressed()
    {
        var (logger, sink) = Create();

        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics")
            .Information("Request starting {Path}{QueryString}", "/odata/hr/employees", "?$filter=email eq 'alice@corp.example'");
        logger.ForContext(Constants.SourceContextPropertyName, "Autheris.Api").Information("app event");

        sink.Events.Count.ShouldBe(1);
        sink.Events[0].MessageTemplate.Text.ShouldBe("app event");
    }

    [Fact]
    public void QueryStringAndUrlProperties_AreRedacted()
    {
        var (logger, sink) = Create();

        logger.Warning("Request {QueryString} {Uri}", "?$filter=email eq 'alice@corp.example'", "https://gw/odata/x?$filter=ssn eq '123'");

        using var writer = new StringWriter();
        sink.Events.Single().RenderMessage(writer);
        writer.ToString().ShouldNotContain("alice@corp.example");
        writer.ToString().ShouldNotContain("123");
        writer.ToString().ShouldContain("https://gw/odata/x?[redacted]");
    }

    [Fact]
    public void AppSettings_ContainsSerilogOverrides()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "src", "Autheris.Api", "appsettings.json"));
        json.ShouldContain("\"Serilog\"");
        json.ShouldContain("\"Microsoft.AspNetCore\": \"Warning\"");
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
