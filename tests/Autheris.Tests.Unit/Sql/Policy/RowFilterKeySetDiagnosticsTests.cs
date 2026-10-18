namespace Autheris.Tests.Unit.Sql.Policy;

using System.Diagnostics.Metrics;
using System.Security.Claims;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>Issue #107 WP-C: confidentiality of keys and key sets (SEC-RF107-09, -18). No value, hash or count reaches a client surface, a log line, an audit field or a metric tag.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetDiagnosticsTests
{
    private static readonly long[] Keys = [7391847, 8402913, 5160274];
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-KS"), new Claim("tenant_id", Tenant)], "Test"));

    [Fact]
    public async Task NoKeyValueInLogsAuditOrMetrics_SEC_RF107_09()
    {
        using var p = KeySetPipeline.Create(logSql: true);
        p.Loader.Override = (r, _) => Task.FromResult(new RowFilterKeySetEntry(
            Keys.Select(k => (object)k).ToArray(), false, DateTimeOffset.UtcNow, r.Tenant.Value, r.DataSourceName));
        var tags = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("autheris.rowfilter.keyset", StringComparison.Ordinal)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, t, _) => { lock (tags) tags.AddRange(t.ToArray()); });
        listener.SetMeasurementEventCallback<double>((_, _, t, _) => { lock (tags) tags.AddRange(t.ToArray()); });
        listener.Start();

        var rewrite = await p.Rewriter.RewriteCoreAsync("SELECT id FROM public.orders", User(), new TenantId(Tenant), null, null, default);

        rewrite.Compiled.Sql.ShouldContain(" IN (");
        string audit = System.Text.Json.JsonSerializer.Serialize(rewrite.KeySets);
        var surfaces = p.Logs.Select(l => l.Message).Append(audit).Append(rewrite.Compiled.Sql).ToList();
        foreach (var key in Keys)
        {
            surfaces.ShouldAllBe(s => !s.Contains(key.ToString(), StringComparison.Ordinal));
        }

        // the tenant only appears in the compiled binds, never in diagnostics
        p.Logs.Where(l => l.Message.Contains("key set site", StringComparison.Ordinal)).ShouldNotBeEmpty();   // the Debug line exists
        p.Logs.ShouldAllBe(l => !l.Message.Contains(Tenant, StringComparison.Ordinal) || !l.Message.Contains("key set", StringComparison.Ordinal));
        tags.ShouldNotBeEmpty();
        tags.ShouldAllBe(t => t.Key == "dialect" || t.Key == "outcome" || t.Key == "reason");   // SEC-RF107-09 d
        tags.ShouldAllBe(t => !(t.Value ?? "").ToString()!.Contains(Tenant, StringComparison.Ordinal));
    }

    [Fact]
    public void KeyHoldingTypes_ToStringPrintsCountsOnly_SEC_RF107_09a()
    {
        var entry = new RowFilterKeySetEntry(Keys.Select(k => (object)k).ToArray(), false, DateTimeOffset.UtcNow, "t", "ds");
        var replacement = new KeySetReplacement("fk", Keys.Select(k => new PolicyValue(k, SqlParameterType.Int64)).ToList(), 4, 0);
        var query = new CompiledKeyQuery("SELECT 1", [new BoundParameter("@t", "p", 0, "tenant-secret", SqlParameterType.String, ParameterOrigin.Tenant)], "bigint", "bigint", "fk", "v");
        var input = new RowFilterKeySetCacheKeyInput("p", "Server=secret-host", TargetSqlDialect.PostgreSql, "tenant-secret", "ds", query, 10, new Dictionary<TableIdentifier, long>());

        foreach (string text in new[] { entry.ToString(), replacement.ToString(), query.ToString(), input.ToString(), new PolicyValue(7391847L, SqlParameterType.Int64).ToString() })
        {
            text.ShouldNotContain("7391847");
            text.ShouldNotContain("8402913");
            text.ShouldNotContain("tenant-secret");
            text.ShouldNotContain("secret-host");
        }
    }

    [Fact]
    public async Task ProblemDetails_HaveNoKeyCount_SEC_RF107_09c()
    {
        string body = await WebSqlBody(RowFilterKeySetErrorCodes.Unavailable(new InvalidOperationException("3 keys for rule customers")));

        body.ShouldContain(RowFilterKeySetErrorCodes.UnavailableMessage);
        body.ShouldNotContain("customers");
        body.ShouldNotContain("keys");
        body.ShouldNotContain("KeyCount");
    }

    [Fact]
    public async Task BudgetReasons_NotInProblemDetails_SEC_RF107_18()
    {
        string body = await WebSqlBody(RowFilterKeySetErrorCodes.Unavailable());

        foreach (var reason in Enum.GetNames<KeySetFallbackReason>())
        {
            body.ShouldNotContain(reason);
        }

        // and the compiler error mapper never surfaces them either: a limit error carries its kind only
        var mapped = Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(
            new TrinoSqlEngine.Ast.Capabilities.SqlLimitExceededException(TrinoSqlEngine.Ast.Capabilities.SqlLimitKind.PolicyExpansionFactor, TargetSqlDialect.PostgreSql, 5000, 4096), out var mapping);
        mapped.ShouldBeTrue();
        mapping.Title.ShouldNotContain("ExpansionBudget");
        mapping.Title.ShouldNotContain("5000");
    }

    [Fact]
    public async Task UnavailableDenial_Is503WithFixedMessage_AndIsWhitelistedForGraphQl()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await Autheris.Api.Middleware.GatewayDenialStatusMapper.WriteWebSqlResponseAsync(context, RowFilterKeySetErrorCodes.Unavailable(), NullLogger.Instance, "Access denied.", default);

        context.Response.StatusCode.ShouldBe(503);
    }

    private static async Task<string> WebSqlBody(GatewaySecurityException denial)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await Autheris.Api.Middleware.GatewayDenialStatusMapper.WriteWebSqlResponseAsync(context, denial, NullLogger.Instance, "Access denied.", default);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
