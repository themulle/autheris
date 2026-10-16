namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedServiceHarness;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// SEC-FED-DELTA-01 / SEC-131-04: the failure audit of a federated query carries a stable code and the exception type
/// only. Exception messages (DuckDB conversion errors quoting staged values, provider text) never reach an audit
/// record or a log line at Information or above, for any exception type and with or without parameters.
/// </summary>
public sealed class FederatedFailureAuditHygieneTests
{
    private const string Sentinel = "SENTINEL-customer-4711-secret-value";

    private static readonly TableMetadata Crm = SqlTable("crm", "public", "customers", "id", "name");
    private static readonly TableMetadata Erp = SqlTable("erp", "public", "orders", "id", "customer_id");

    private const string Join = "FROM crm.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id";

    private static (Pipeline Pipeline, List<(LogLevel Level, string Message)> Logs) Setup(StubTableConnector? crm = null)
    {
        crm ??= new StubTableConnector("crm", [Row(("id", "1"), ("name", Sentinel))]);
        var erp = new StubTableConnector("erp", [Row(("id", "10"), ("customer_id", "1"))]);
        var logs = new List<(LogLevel Level, string Message)>();
        var pipeline = Build([(Crm, crm), (Erp, erp)], Consents(Allow(Crm.Identifier), Allow(Erp.Identifier)), logs: logs);
        return (pipeline, logs);
    }

    private static List<AuditLogEntry> Entries(Pipeline pipeline) =>
        pipeline.Audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogRepository.RecordAuditEventAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    private static void AssertClean(Pipeline pipeline, List<(LogLevel Level, string Message)> logs)
    {
        var entries = Entries(pipeline);
        entries.ShouldContain(e => e.EventType == AuditEventTypes.QueryExecutionError);
        entries.ShouldAllBe(e => e.DetailsJson == null || !e.DetailsJson.Contains(Sentinel));
        logs.Where(l => l.Level >= LogLevel.Information).ShouldAllBe(l => !l.Message.Contains(Sentinel));
    }

    [Fact]
    public async Task DuckDbConversionError_QuotingAStagedValue_LeavesNoSentinelInAuditOrLogs()
    {
        var (pipeline, logs) = Setup();

        var ex = await Should.ThrowAsync<Exception>(() => pipeline.RunFullAsync($"SELECT CAST(c.name AS INTEGER) {Join}"));

        ex.ToString().ShouldContain(Sentinel); // premise: the engine error really quotes the staged value
        AssertClean(pipeline, logs);
        var details = Entries(pipeline).Single(e => e.EventType == AuditEventTypes.QueryExecutionError).DetailsJson!;
        details.ShouldContain("\"reasonCode\"");
    }

    [Fact]
    public async Task ProviderException_QuotingAValue_LeavesNoSentinelInAuditOrLogs()
    {
        var crm = new StubTableConnector("crm", [], _ => throw new InvalidOperationException($"ORA-00001: duplicate value '{Sentinel}'"));
        var (pipeline, logs) = Setup(crm);

        await Should.ThrowAsync<Exception>(() => pipeline.RunFullAsync($"SELECT c.id {Join}"));

        AssertClean(pipeline, logs);
    }

    [Fact]
    public async Task FailureAudit_ErrorField_IsTypeNameOnly()
    {
        var crm = new StubTableConnector("crm", [], _ => throw new InvalidOperationException(Sentinel));
        var (pipeline, _) = Setup(crm);

        await Should.ThrowAsync<Exception>(() => pipeline.RunFullAsync($"SELECT c.id {Join}"));

        var details = System.Text.Json.JsonDocument.Parse(Entries(pipeline).Single(e => e.EventType == AuditEventTypes.QueryExecutionError).DetailsJson!).RootElement;
        details.GetProperty("reasonCode").GetString().ShouldNotBeNullOrWhiteSpace();
        (details.GetProperty("error").GetString() ?? string.Empty).ShouldNotContain(Sentinel);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Security.SecurityException))]
    [InlineData(typeof(TimeoutException))]
    public async Task WebSqlErrorEndpoint_LogsOnlyTypeAndTraceAboveDebug_AndNeverReturnsTheMessage(Type exceptionType)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new System.IO.MemoryStream();
        context.TraceIdentifier = "trace-b0";
        var exception = (Exception)Activator.CreateInstance(exceptionType, $"conversion failed for value '{Sentinel}'")!;

        await Autheris.Api.Endpoints.WebSqlEndpoints.WriteWebSqlErrorAsync(
            context, exception, new ListLogger<object>(logs), System.Threading.CancellationToken.None);

        context.Response.Body.Position = 0;
        (await new System.IO.StreamReader(context.Response.Body).ReadToEndAsync()).ShouldNotContain(Sentinel);
        logs.Where(l => l.Level >= LogLevel.Information).ShouldAllBe(l => !l.Message.Contains(Sentinel));
        logs.ShouldContain(l => l.Level >= LogLevel.Warning && l.Message.Contains(exceptionType.Name) && l.Message.Contains("trace-b0"));
    }
}
