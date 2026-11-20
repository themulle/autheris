namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
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

    [Fact]
    public void Pseudonymization_key_resolves_from_vault_ref_and_fails_closed_when_missing()
    {
        var expectedKey = RandomNumberGenerator.GetBytes(32);
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes("kv://usage-pseudonym-key").Returns(expectedKey);

        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Production" });
        services.AddSingleton(Options.Create(new UsageAccountingOptions
        {
            PseudonymizeUsers = true,
            PseudonymizationKeyVaultRef = "kv://usage-pseudonym-key"
        }));
        services.AddSingleton(secretProvider);
        using var sp = services.BuildServiceProvider();

        var resolved = GatewayGovernanceServiceExtensions.ResolveUsagePseudonymizationKey(sp);
        resolved.ShouldBe(expectedKey);

        // Missing secret provider fails closed (no fallback to Cluster:Secret or random key).
        var noProviderServices = new ServiceCollection();
        noProviderServices.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Production" });
        noProviderServices.AddSingleton(Options.Create(new UsageAccountingOptions
        {
            PseudonymizeUsers = true,
            PseudonymizationKeyVaultRef = "kv://usage-pseudonym-key"
        }));
        using var noProviderSp = noProviderServices.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() =>
            GatewayGovernanceServiceExtensions.ResolveUsagePseudonymizationKey(noProviderSp));

        // Short key outside Development fails closed.
        var shortProvider = Substitute.For<IKeyVaultSecretProvider>();
        shortProvider.GetSecretBytes("kv://usage-pseudonym-key").Returns(RandomNumberGenerator.GetBytes(8));
        var shortKeyServices = new ServiceCollection();
        shortKeyServices.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = "Production" });
        shortKeyServices.AddSingleton(Options.Create(new UsageAccountingOptions
        {
            PseudonymizeUsers = true,
            PseudonymizationKeyVaultRef = "kv://usage-pseudonym-key"
        }));
        shortKeyServices.AddSingleton(shortProvider);
        using var shortKeySp = shortKeyServices.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() =>
            GatewayGovernanceServiceExtensions.ResolveUsagePseudonymizationKey(shortKeySp));

        // Legacy Gateway:Audit:ActorPseudonymizationKey is rejected as an unknown key.
        var legacyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Audit:ActorPseudonymizationKey"] = "legacy" })
            .Build();
        Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(legacyConfig));
    }
}
