namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #341: valid but argument-light GraphQL requests answered INTERNAL_SERVER_ERROR. The host mirrors the sim API host (SQLite data
/// source "default", the default seeded catalog whose "finance", "hr" and "governance" sources have no connection). Every request must
/// answer a governed code, never INTERNAL_SERVER_ERROR; an unknown and a forbidden table stay indistinguishable. No SQL Server or Docker needed.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue341GraphQlInternalErrorTests : IDisposable
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"autheris-i341-{Guid.NewGuid():N}.db");
    private readonly string _dataFile = Path.Combine(Path.GetTempPath(), $"autheris-i341-data-{Guid.NewGuid():N}.db");
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), "autheris-i341-*").Where(f => f.Contains(Path.GetFileName(_dbFile)) || f.Contains(Path.GetFileName(_dataFile))))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    public static TheoryData<string> UnbackedTableQueries => new()
    {
        "{ finance_dbo_finance_items(first: 1, offset: 0) { id product_name } }",
        "{ governance_system_policies(first: 1, offset: 0) { id classification_tags } }",
        "{ governance_system_datasources(first: 1, offset: 0) { id name } }",
    };

    public static TheoryData<string> EmptyArgumentQueries => new()
    {
        "{ table(domain: \"\", name: \"\", schema: \"\", first: 0, after: 0) { jsonRows } }",
        "{ table(domain: \"a.b\", name: \"t\", schema: \"dbo\") { jsonRows } }",
        "{ tableConsumers(domain: \"a.b\", schema: \"dbo\", tableName: \"t\") { __typename } }",
        "{ myAccess(table: \"\") { consentDescriptionOrigin } }",
        "{ myActiveConsents(table: \"\", first: 0, after: 0) { __typename } }",
        "{ tableConsumers(domain: \"\", schema: \"\", tableName: \"\") { __typename } }",
        "{ gdprDataDisclosureReport(domain: \"\", schema: \"\", tableName: \"\", subjectSid: \"\") { __typename } }",
        "{ exportGdprDataDisclosureReportPdfBase64(domain: \"\", schema: \"\", tableName: \"\", subjectSid: \"\") }",
        "{ finance { invoices(first: 0, after: 0) { jsonRows } } }",
        "{ hr { employees(first: 0, after: 0) { jsonRows } } }",
    };

    public static TheoryData<string> UnknownIdMutations => new()
    {
        "mutation { rejectConsentRequest(requestId: \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\", reason: \"\") { __typename } }",
        "mutation { rejectConsentRequest(requestId: \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\", reason: \"no longer needed\") { __typename } }",
        "mutation { requestTableAccess(domain: \"\", schema: \"\", tableName: \"\", justification: \"\", durationDays: 0) { __typename } }",
        "mutation { requestTableAccess(domain: \"\", schema: \"\", tableName: \"\", justification: \"audit evidence\", durationDays: 7) { __typename } }",
        "mutation { requestTableAccess(domain: \"a.b\", schema: \"dbo\", tableName: \"t\", justification: \"audit evidence\", durationDays: 7) { __typename } }",
        "mutation { requestTableAccess(domain: \"finance\", schema: \"dbo\", tableName: \"nope\", justification: \"\", durationDays: 0) { __typename } }",
        "mutation { revokeConsent(consentId: \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\", reason: \"\") }",
        "mutation { revokeConsent(consentId: \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\", reason: \"no longer needed\") }",
        "mutation { syncDataCatalog(dryRun: false) { __typename } }",
        "query { calculateConsentRevocationImpact(consentId: \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\") { __typename } }",
    };

    [Theory]
    [MemberData(nameof(UnbackedTableQueries))]
    public async Task UnbackedCatalogTable_IsGovernedDenial_NotInternalError(string query) =>
        await AssertGovernedAsync(query, "Reader");

    [Theory]
    [MemberData(nameof(EmptyArgumentQueries))]
    public async Task EmptyArguments_AreGovernedErrors_NotInternalError(string query) =>
        await AssertGovernedAsync(query, "Reader");

    [Theory]
    [MemberData(nameof(UnknownIdMutations))]
    public async Task UnknownIdsAndEmptyText_AreGovernedErrors_NotInternalError(string query) =>
        await AssertGovernedAsync(query, "GovernanceAdmin");

    [Theory]
    [InlineData("{ table(domain: \"a.b\", name: \"orders\", schema: \"dbo\") { jsonRows } }")]
    [InlineData("{ table(domain: \"\", name: \"\", schema: \"\") { jsonRows } }")]
    [InlineData("{ tableConsumers(domain: \"x\\ny\", schema: \"dbo\", tableName: \"t\") { __typename } }")]
    public async Task Query_EmptyOrMalformedTableArgument_BadRequest_NoInputEcho(string query)
    {
        var (_, codes, messages) = await SendAsync(query, "Reader");

        codes.ShouldBe(["BAD_REQUEST"]);
        messages.ShouldBe(["The table identifier is invalid."]);
    }

    [Fact]
    public async Task UnbackedSource_AnswersAccessDenied_SameForEveryUnbackedEntry_NoSourceNameInResponse()
    {
        var finance = await SendAsync("{ finance_dbo_finance_items(first: 1, offset: 0) { id } }", "Reader");
        var governance = await SendAsync("{ governance_system_policies(first: 1, offset: 0) { id } }", "Reader");

        finance.Codes.ShouldBe(["ACCESS_DENIED"]);
        governance.Codes.ShouldBe(finance.Codes);
        governance.Messages.ShouldBe(finance.Messages);
        finance.Messages.ShouldBe(["Access denied."]);
        finance.Body.ShouldNotContain("no database connection");
        governance.Body.ShouldNotContain("no database connection");
    }

    private async Task AssertGovernedAsync(string query, string roles)
    {
        var (body, codes, _) = await SendAsync(query, roles);
        var faults = _logs.Faults.ToArray();
        codes.ShouldNotContain("INTERNAL_SERVER_ERROR", $"{query} body={body} threw: {string.Join(" | ", faults)}");
        body.ShouldNotContain("no database connection");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ");
    }

    private async Task<(string Body, List<string> Codes, List<string> Messages)> SendAsync(string query, string roles, WebApplicationFactory<Program>? host = null)
    {
        using var owned = host == null ? Create() : null;
        var factory = host ?? owned!;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SIM");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sim");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/graphql", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();

        var codes = new List<string>();
        var messages = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            foreach (var e in errors.EnumerateArray())
            {
                codes.Add(e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "");
                messages.Add(e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
            }
        }

        return (body, codes, messages);
    }

    [Fact]
    public async Task PolicyDeniedTable_AndUnbackedTable_AreIndistinguishableForTheSameCaller()
    {
        // The catalog entry is backed (source "default") and exists, but the caller has no grant and consent checks are on.
        var id = new Autheris.Domain.Common.TableIdentifier("default", "main", "orders");
        using (var seeding = Create(bypassConsent: false))
        {
            using var scope = seeding.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<Autheris.Application.Interfaces.ITableMetadataRepository>();
            var tableId = Guid.NewGuid();
            await repo.UpsertTableMetadataAsync(new Autheris.Domain.Model.TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = tableId, DisplayName = "orders", TableName = "orders", SchemaName = "main", SourceType = "Sqlite", SourceName = "default", IsActive = true },
                Identifier = id,
                Columns = [new Autheris.Domain.Model.TableColumn { TableId = tableId, ColumnName = "id", DataType = "integer" }],
            });
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var host = Create(bypassConsent: false);
        var denied = await SendAsync("{ default_main_orders(first: 1, offset: 0) { id } }", "Reader", host);
        var unbacked = await SendAsync("{ finance_dbo_finance_items(first: 1, offset: 0) { id } }", "Reader", host);

        denied.Codes.ShouldBe(["ACCESS_DENIED"], denied.Body);
        unbacked.Codes.ShouldBe(denied.Codes);
        unbacked.Messages.ShouldBe(denied.Messages);
        unbacked.Messages.ShouldBe(["Access denied."]);
    }

    private WebApplicationFactory<Program> Create(bool bypassConsent = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
            b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:MaxCostPerMinute", "1000000");
            b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={_dbFile}");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", bypassConsent ? "true" : "false");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            b.UseSetting("Gateway:OpenSchema", "true");
            b.UseSetting("Gateway:Tenancy:Mode", "Multi");
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataFile}");
            b.ConfigureLogging(l => l.AddProvider(_logs));
        });

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Faults { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception == null)
                {
                    return;
                }

                var frame = string.Join(" <- ", (exception.StackTrace ?? string.Empty).Split('\n').Where(l => l.Contains("Autheris.", StringComparison.Ordinal)).Take(2).Select(l => l.Trim().Split(" in ")[0]));
                owner.Faults.Enqueue($"{exception.GetType().Name}: {exception.Message} @ {frame}");
            }
        }
    }
}
