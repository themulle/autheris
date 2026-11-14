namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CallerLabelProvenanceTests
{
    private static DefaultHttpContext Request(string? tokenClientId, string claimType = "azp", params (string Header, string Value)[] headers)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001") };
        if (tokenClientId is not null)
        {
            claims.Add(new Claim(claimType, tokenClientId));
        }

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }

        return context;
    }

    [Fact]
    public void Audit_record_separates_asserted_label_and_token_client_id()
    {
        var principalContext = SecurityContextFactory.CreateFromHttpContext(
            Request(
                "22222222-2222-2222-2222-222222222222",
                "azp",
                (SecurityContextFactory.CallerAppHeaderName, "powerbi-spoofed"),
                (SecurityContextFactory.CallerVersionHeaderName, "2.5"),
                (SecurityContextFactory.CallerQueryHeaderName, "ExportPayroll")));

        principalContext.Caller.ShouldNotBeNull();
        principalContext.Caller.IsClientAsserted.ShouldBeTrue();
        principalContext.Caller.ClientId.ShouldBe("22222222-2222-2222-2222-222222222222");
        principalContext.Caller.Application.ShouldBe("powerbi-spoofed");

        var entry = new AuditLogEntry
        {
            EventType = "TABLE_QUERY",
            Caller = principalContext.Caller
        };

        using var doc = JsonDocument.Parse(entry.EffectiveDetailsJson);
        var caller = doc.RootElement.GetProperty("caller");
        caller.GetProperty("app").GetString().ShouldBe("powerbi-spoofed");
        caller.GetProperty("ver").GetString().ShouldBe("2.5");
        caller.GetProperty("query").GetString().ShouldBe("exportpayroll");
        caller.GetProperty("asserted").GetBoolean().ShouldBeTrue();
        caller.GetProperty("clientId").GetString().ShouldBe("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void Caller_supplied_caller_key_in_details_json_is_overwritten()
    {
        var entry = new AuditLogEntry
        {
            EventType = "TABLE_QUERY",
            DetailsJson = """{"caller":{"app":"forged","asserted":false,"clientId":"forged-id"},"rowCount":42}""",
            Caller = new CallerContext("reporting", "1.0", "daily", "22222222-2222-2222-2222-222222222222")
            {
                IsClientAsserted = true
            }
        };

        using var doc = JsonDocument.Parse(entry.EffectiveDetailsJson);
        doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(42);
        var caller = doc.RootElement.GetProperty("caller");
        caller.GetProperty("app").GetString().ShouldBe("reporting");
        caller.GetProperty("asserted").GetBoolean().ShouldBeTrue();
        caller.GetProperty("clientId").GetString().ShouldBe("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void Usage_key_never_uses_asserted_label()
    {
        var callerA = SecurityContextFactory.CreateFromHttpContext(
            Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "spoofed-powerbi"))).Caller!;
        var callerB = SecurityContextFactory.CreateFromHttpContext(
            Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "spoofed-excel"))).Caller!;

        var aggregator = new UsageAggregator();
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-1-2-3-1001"), new HashSet<Sid>());

        aggregator.Record(identity, DataSourceTransportFlags.Rest, callerA, durationMs: 10, bytes: 100, statusCode: 200, facts: []);
        aggregator.Record(identity, DataSourceTransportFlags.Rest, callerB, durationMs: 15, bytes: 200, statusCode: 200, facts: []);

        var snapshot = aggregator.Swap();
        snapshot.Series.Count.ShouldBe(1);
        var entry = snapshot.Series.Single();
        entry.Key.Application.ShouldNotContain("spoofed-powerbi");
        entry.Key.Application.ShouldNotContain("spoofed-excel");
        entry.Key.ClientId.ShouldBe("22222222-2222-2222-2222-222222222222");
        entry.Value.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task Asserted_label_never_in_rate_limit_or_deny_key()
    {
        var rateLimiter = new InMemoryRateLimiterService();
        var options = Options.Create(new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                PostAuthSidRateLimit = new PostAuthSidRateLimitOptions { TokenBucketCapacity = 1, TokensPerSecond = 1 }
            }
        });

        var middleware = new PostAuthSidRateLimitingMiddleware(_ => Task.CompletedTask, options, rateLimiter);

        var req1 = Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "label-a"));
        req1.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(req1);
        req1.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var req2 = Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "label-b"));
        req2.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(req2);
        req2.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);

        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()));
        var meta = new TableMetadata
        {
            Table = new Table { SourceName = "default", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Identifier = new TableIdentifier("default", "dbo", "orders")
        };
        var principalA = SecurityContextFactory.CreateFromHttpContext(
            Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "admin-console")));
        var principalB = SecurityContextFactory.CreateFromHttpContext(
            Request("22222222-2222-2222-2222-222222222222", "azp", (SecurityContextFactory.CallerAppHeaderName, "other-app")));

        var decA = await pdp.DecideAsync(new AccessRequest(principalA, meta, AccessOperation.Read, AccessTransport.Rest));
        var decB = await pdp.DecideAsync(new AccessRequest(principalB, meta, AccessOperation.Read, AccessTransport.Rest));
        decA.IsAllowed.ShouldBeFalse();
        decB.IsAllowed.ShouldBe(decA.IsAllowed);
        decB.Reasons.ShouldBe(decA.Reasons);
    }

    [Fact]
    public void Consumer_view_groups_by_client_id()
    {
        var day = "2026-11-14";
        var actorsA = new HashSet<string>(StringComparer.Ordinal) { "h1", "h2" };
        var actorsB = new HashSet<string>(StringComparer.Ordinal) { "h3", "h4" };
        var actorsC = new HashSet<string>(StringComparer.Ordinal) { "h5", "h6", "h7" };

        var summaries = new List<ConsumerAppDailySummary>
        {
            new("spoofed-a", day, 10, 100, 10, new RejectionBreakdown(1, 0, 0, 0), ["1.0"], ["Q1"], DistinctActors: 2)
            {
                ClientId = "22222222-2222-2222-2222-222222222222",
                IsClientAsserted = true,
                ActorKeys = actorsA
            },
            new("spoofed-b", day, 5, 50, 5, new RejectionBreakdown(0, 1, 0, 0), ["2.0"], ["Q2"], DistinctActors: 2)
            {
                ClientId = "22222222-2222-2222-2222-222222222222",
                IsClientAsserted = true,
                ActorKeys = actorsB
            },
            // Same asserted label without a token clientId must NOT merge with clientId-backed application rows
            new("powerbi", day, 7, 70, 7, new RejectionBreakdown(), ["1.0"], ["Q3"], DistinctActors: 1)
            {
                ClientId = null,
                IsClientAsserted = true,
                ActorKeys = new HashSet<string>(StringComparer.Ordinal) { "h8" }
            },
            new("powerbi", day, 20, 200, 20, new RejectionBreakdown(), ["1.0"], ["Q4"], DistinctActors: 3)
            {
                ClientId = "33333333-3333-3333-3333-333333333333",
                IsClientAsserted = true,
                ActorKeys = actorsC
            }
        };

        // Privileged tier grouping preserves separate groups for authenticated clientIds vs unauthenticated asserted label
        var grouped = Autheris.Application.Lineage.ConsumerFolding.GroupByClient(summaries);
        grouped.Count.ShouldBe(3);

        var mergedClient = grouped.Single(x => x.ClientId == "22222222-2222-2222-2222-222222222222");
        mergedClient.Calls.ShouldBe(15);
        mergedClient.DistinctActors.ShouldBe(4);
        mergedClient.Application.ShouldBe("22222222-2222-2222-2222-222222222222");
        mergedClient.IsClientAsserted.ShouldBe(false);

        var unauthPowerBi = grouped.Single(x => x.ClientId == null && x.Application == "powerbi");
        unauthPowerBi.Calls.ShouldBe(7);
        unauthPowerBi.IsClientAsserted.ShouldBe(true);

        var authPowerBi = grouped.Single(x => x.ClientId == "33333333-3333-3333-3333-333333333333");
        authPowerBi.Calls.ShouldBe(20);
        authPowerBi.Application.ShouldBe("powerbi");
        authPowerBi.IsClientAsserted.ShouldBe(true);

        // K-anonymity folding keys on the clientId group (2+2=4 >= k=3 stays published; singleton asserted row < k=3 folds & suppresses)
        var (folded, suppressed) = Autheris.Application.Lineage.ConsumerFolding.Fold(summaries, minActors: 3);
        suppressed.ShouldBeTrue();
        folded.ShouldContain(x => x.ClientId == "22222222-2222-2222-2222-222222222222" && x.Calls == 15);
        folded.ShouldContain(x => x.ClientId == "33333333-3333-3333-3333-333333333333" && x.Calls == 20);
        folded.ShouldContain(x => x.Application == Autheris.Application.Lineage.ConsumerFolding.DefaultOtherBucketName && x.Calls == 0);
    }
}
