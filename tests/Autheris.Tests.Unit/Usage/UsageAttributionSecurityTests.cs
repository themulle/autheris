namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageAttributionSecurityTests
{
    private static SecurityPrincipalContext CreateContext(
        TenantId? tenant,
        IReadOnlySet<Sid>? groupSids = null,
        bool isAuthenticated = true) => new()
    {
        UserSid = new Sid("S-1-5-21-TRUSTED-USER"),
        ResolvedTenant = tenant,
        GroupSids = groupSids ?? new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AuthenticationScheme = isAuthenticated ? "Bearer" : "Anonymous",
        IsAuthenticated = isAuthenticated
    };

    [Fact]
    public async Task Group_sids_only_from_security_context()
    {
        var aggregator = new UsageAggregator();
        var trustedGroup = new Sid("S-1-5-21-REAL-GROUP");
        var spoofedGroup = new Sid("S-1-5-21-SPOOFED-GROUP");
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Request.Headers["X-Groups"] = spoofedGroup.Value;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("groups", spoofedGroup.Value),
            new Claim("sid", "S-1-5-21-SPOOFED-USER")
        ], "Bearer"));
        context.Items[SecurityPrincipalContext.ItemKey] = CreateContext(
            new TenantId("tenant-trusted"),
            new HashSet<Sid> { trustedGroup });

        var middleware = new UsageMeteringMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, aggregator);

        var snapshot = aggregator.Swap();
        snapshot.GroupSets.ShouldNotBeEmpty();
        var recordedGroups = snapshot.GroupSets.Values.SelectMany(g => g).ToList();
        recordedGroups.ShouldContain(trustedGroup);
        recordedGroups.ShouldNotContain(spoofedGroup);
        snapshot.Series.Keys.Single().UserKey.ShouldBe("S-1-5-21-TRUSTED-USER");
    }

    [Fact]
    public async Task Tenant_from_principal_not_from_header_or_query()
    {
        var aggregator = new UsageAggregator();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Request.Headers["X-Tenant-ID"] = "spoofed-header-tenant";
        context.Request.QueryString = new QueryString("?tenant=spoofed-query-tenant");
        context.Items[SecurityPrincipalContext.ItemKey] = CreateContext(new TenantId("trusted-principal-tenant"));

        var middleware = new UsageMeteringMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, aggregator);

        var series = aggregator.Swap().Series.ShouldHaveSingleItem();
        series.Key.Tenant.ShouldBe(new TenantId("trusted-principal-tenant"));
    }

    [Fact]
    public async Task Anonymous_requests_not_attributed()
    {
        long unattributedIncremented = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == GatewayDiagnostics.Meter.Name &&
                    instrument.Name == "autheris_usage_unattributed_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref unattributedIncremented, value));
        listener.Start();

        var aggregator = new UsageAggregator();
        var middleware = new UsageMeteringMiddleware(_ =>
        {
            UsageRequestScope.Current.ShouldBeNull();
            return Task.CompletedTask;
        });

        var missingCtx = new DefaultHttpContext();
        missingCtx.Request.Path = "/api/v1/query";
        await middleware.InvokeAsync(missingCtx, aggregator);

        var anonCtx = new DefaultHttpContext();
        anonCtx.Request.Path = "/api/v1/query";
        anonCtx.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.Anonymous();
        await middleware.InvokeAsync(anonCtx, aggregator);

        var unresolvedTenantCtx = new DefaultHttpContext();
        unresolvedTenantCtx.Request.Path = "/api/v1/query";
        unresolvedTenantCtx.Items[SecurityPrincipalContext.ItemKey] = CreateContext(tenant: null, isAuthenticated: true);
        await middleware.InvokeAsync(unresolvedTenantCtx, aggregator);

        aggregator.Swap().Series.ShouldBeEmpty();
        Interlocked.Read(ref unattributedIncremented).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void No_default_pseudonymization_secret_in_source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Expected to locate repository root containing 'src/'");
        var srcDir = Path.Combine(dir.FullName, "src");

        const string legacyDefaultSecret = "autheris-default-cluster-telemetry-secret-key-2026";
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            if (content.Contains(legacyDefaultSecret, StringComparison.Ordinal) ||
                content.Contains("IConsumerUsageRecorder", StringComparison.Ordinal))
            {
                matches.Add(Path.GetRelativePath(dir.FullName, file));
            }
        }

        matches.ShouldBeEmpty();
    }

    [Fact]
    public void Metering_adds_no_await_on_hot_path()
    {
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Items[SecurityPrincipalContext.ItemKey] = CreateContext(new TenantId("tenant-1"));

        bool recordedSync = false;
        var middleware = new UsageMeteringMiddleware(_ =>
        {
            recorder.Record(
                new UsageFact(new TableIdentifier("sales", "dbo", "orders"), "sales", UsageOutcome.Allow, 5, true, 0, Array.Empty<GrantRef>(), default),
                new TenantId("tenant-1"),
                new Sid("S-1-5-21-TRUSTED-USER"));
            recordedSync = true;
            return Task.CompletedTask;
        });

        Task invokeTask = middleware.InvokeAsync(context, aggregator);
        invokeTask.IsCompletedSuccessfully.ShouldBeTrue("Synchronous pipeline completion must not introduce async state machine yields.");
        recordedSync.ShouldBeTrue();
        typeof(IUsageRecorder).GetMethod(nameof(IUsageRecorder.Record))!.ReturnType.ShouldBe(typeof(void));
        UsageRequestScope.Current.ShouldBeNull();
    }
}
