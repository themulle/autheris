namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Plugins.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class AuthPluginSecurityTests
{
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class StubRoleProvider(string name, PluginRoleEnrichmentResult result, bool throws = false, TimeSpan? delay = null)
        : IPluginRoleProvider
    {
        public string Name => name;

        public async ValueTask<PluginRoleEnrichmentResult> EnrichRolesAsync(PluginSecurityContext context, CancellationToken ct)
        {
            if (delay.HasValue)
            {
                await Task.Delay(delay.Value, ct).ConfigureAwait(false);
            }

            if (throws)
            {
                throw new SecurityException("Exploit triggered in role provider");
            }

            return result;
        }
    }

    private sealed class StubAuthHook(
        string name,
        int order,
        Func<PluginAuthorizationContext, CancellationToken, ValueTask<PluginAuthorizationResult>> behavior)
        : IPluginAuthorizationHook
    {
        public int InvocationCount;
        public string HookName => name;
        public int Order => order;

        public async ValueTask<PluginAuthorizationResult> EvaluateAccessAsync(PluginAuthorizationContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref InvocationCount);
            return await behavior(context, ct);
        }
    }

    private sealed class StubPluginCallerAttributeProvider(string kind, bool throws = false)
        : IPluginCallerAttributeProvider
    {
        public string Kind => kind;

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            if (throws)
            {
                throw new InvalidOperationException("Exploit in attribute provider");
            }

            return ValueTask.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue(["val"], SqlParameterType.String)));
        }
    }

    // T-SEC-BL08-01: SEC-BL08-01 - Unpinned assembly in non-dev environment throws SecurityException
    [Fact]
    public void T_SEC_BL08_01_UnpinnedAssembly_InNonDevEnvironment_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "autheris_test_unpinned_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "MaliciousPlugin.dll");
            File.WriteAllBytes(dllPath, [0x4d, 0x5a, 0x90, 0x00]);

            var options = new GatewayOptions
            {
                Plugins = new PluginsOptions
                {
                    Directory = tempDir,
                    RequireIntegrityManifest = true
                }
            };

            var services = new ServiceCollection();
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment { EnvironmentName = "Production" });
            var sp = services.BuildServiceProvider();

            var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance, serviceProvider: sp);

            var ex = Should.Throw<SecurityException>(() => host.LoadPluginsFromDirectory(tempDir));
            ex.Message.ShouldContain("Security error");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // T-SEC-BL08-02: SEC-BL08-01 - Mismatched hash assembly throws SecurityException
    [Fact]
    public void T_SEC_BL08_02_MismatchedHashAssembly_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "autheris_test_mismatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "TamperedPlugin.dll");
            File.WriteAllBytes(dllPath, [0x4d, 0x5a, 0x90, 0x00, 0x01, 0x02]);

            var options = new GatewayOptions
            {
                Plugins = new PluginsOptions
                {
                    Directory = tempDir,
                    TrustedPluginHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TamperedPlugin.dll"] = "0000000000000000000000000000000000000000000000000000000000000000" // Wrong hash
                    }
                }
            };

            var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);

            var ex = Should.Throw<SecurityException>(() => host.LoadPluginsFromDirectory(tempDir));
            ex.Message.ShouldContain("Integrity check failed");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // T-SEC-BL08-03: SEC-BL08-01 - ReadVerifiedBytes loads directly into memory stream (No TOCTOU)
    [Fact]
    public void T_SEC_BL08_03_ReadVerifiedBytes_LoadsDirectlyFromMemoryStream_NoTOCTOU()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "autheris_test_toctou_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "ValidPlugin.dll");
            byte[] fileBytes = [0x4d, 0x5a, 0x90, 0x00, 0x03, 0x04];
            File.WriteAllBytes(dllPath, fileBytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(fileBytes));

            var trustList = new Autheris.Infrastructure.Plugins.PluginTrustList(
                tempDir,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ValidPlugin.dll"] = expectedHash });

            var verifiedBytes = trustList.ReadVerifiedBytes(dllPath);

            verifiedBytes.ShouldBe(fileBytes);
            using var stream = new MemoryStream(verifiedBytes, writable: false);
            stream.Length.ShouldBe(fileBytes.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // T-SEC-BL08-04: SEC-BL08-02 - RoleProvider unhandled exception fails closed with 403
    [Fact]
    public async Task T_SEC_BL08_04_RoleProvider_UnhandledException_FailsClosedWith403()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);
        host.RegisterRoleProvider(new StubRoleProvider("bad_provider", PluginRoleEnrichmentResult.Empty, throws: true));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthPluginHost>(host);
        services.AddSingleton(Options.Create(options));
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(options);
        services.AddSingleton(monitor);
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("tid", "tenant-1")], "Bearer"));

        var middleware = new SecurityContextResolutionMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(httpContext);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        body.ShouldContain("AUTH_PLUGIN_FAILED");
    }

    // T-SEC-BL08-05: SEC-BL08-02 - AttributeProvider unhandled exception converges to deny predicate
    [Fact]
    public async Task T_SEC_BL08_05_AttributeProvider_UnhandledException_ConvergesToDenyPredicate()
    {
        var options = new GatewayOptions
        {
            CallerAttributes = new CallerAttributeOptions
            {
                Enabled = true,
                Claims = new CallerAttributeClaimsOptions
                {
                    TrustedIssuers = [new CallerAttributeTrustedIssuer { Issuer = "https://login.example/tenant/v2.0", SubjectKeyIsPerson = true }]
                }
            },
            Plugins = new PluginsOptions
            {
                Auth = new AuthPluginOptions { Enabled = true }
            }
        };
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);
        host.RegisterCallerAttributeProvider(new StubPluginCallerAttributeProvider("throwing_attr", throws: true));

        var def = new CallerAttributeDefinition(
            Guid.NewGuid(), new TenantId("tenant-1"), "sec_level",
            CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60,
            new CallerAttributeProviderBinding("throwing_attr", new Dictionary<string, string>()), null, FilterApprovalStatus.Active);

        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        using var resolver = new CallerAttributeResolver(registry, [], Options.Create(options), null, null, host);

        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("oid", "11111111-1111-1111-1111-111111111111", ClaimValueTypes.String, "https://login.example/tenant/v2.0"));
        var subject = CallerSubjectFactory.Create(
            new ClaimsPrincipal(identity),
            new TenantId("tenant-1"),
            options.CallerAttributes);

        var set = await resolver.ResolveAsync(subject, ["sec_level"], CancellationToken.None);
        set.TryGet("sec_level", out var attrResult).ShouldBeTrue();

        attrResult.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
    }

    // T-SEC-BL08-06: SEC-BL08-02 - AuthorizationHook unhandled exception returns Denied decision
    [Fact]
    public async Task T_SEC_BL08_06_AuthorizationHook_UnhandledException_ReturnsDeniedDecision()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);
        host.RegisterAuthorizationHook(new StubAuthHook("crashing_hook", 1, (_, _) =>
            throw new SecurityException("Hook crashed intentionally")));

        var policy = CreateAllowingPolicy(options, host);
        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("AUTH_PLUGIN_FAILED"));
    }

    // T-SEC-BL08-07: SEC-BL08-03 - RoleProvider execution timeout aborts with 403 and unblocks
    [Fact]
    public async Task T_SEC_BL08_07_RoleProvider_ExecutionTimeout_AbortsWith403AndUnblocks()
    {
        var timeProvider = new FakeTimeProvider();
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance, timeProvider: timeProvider);
        host.RegisterRoleProvider(new StubRoleProvider("slow_provider", PluginRoleEnrichmentResult.Empty, delay: TimeSpan.FromMinutes(5)));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthPluginHost>(host);
        services.AddSingleton(Options.Create(options));
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(options);
        services.AddSingleton(monitor);
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("tid", "tenant-1")], "Bearer"));

        var middleware = new SecurityContextResolutionMiddleware(_ => Task.CompletedTask);
        var middlewareTask = middleware.InvokeAsync(httpContext);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await middlewareTask;

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        body.ShouldContain("AUTH_PLUGIN_TIMEOUT");
    }

    // T-SEC-BL08-08: SEC-BL08-03 - AuthorizationHook execution timeout returns Denied decision
    [Fact]
    public async Task T_SEC_BL08_08_AuthorizationHook_ExecutionTimeout_ReturnsDeniedDecision()
    {
        var timeProvider = new FakeTimeProvider();
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance, timeProvider: timeProvider);
        host.RegisterAuthorizationHook(new StubAuthHook("slow_hook", 1, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return PluginAuthorizationResult.Allow();
        }));

        var policy = CreateAllowingPolicy(options, host);
        var decideTask = policy.DecideAsync(CreateQuery(), CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var decision = await decideTask;

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("AUTH_PLUGIN_TIMEOUT"));
    }

    // T-SEC-BL08-09: SEC-BL08-04 - Multiple role providers: one fails -> entire enrichment fails closed
    [Fact]
    public async Task T_SEC_BL08_09_MultipleRoleProviders_OneFails_EntireEnrichmentFailsClosed()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);
        host.RegisterRoleProvider(new StubRoleProvider("good", new PluginRoleEnrichmentResult(["GoodRole"])));
        host.RegisterRoleProvider(new StubRoleProvider("failing", PluginRoleEnrichmentResult.Empty, throws: true));

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

        var result = await host.EnrichRolesAsync(context, CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("AUTH_PLUGIN_FAILED");
        result.Result.AddedRoles.ShouldBeEmpty();
    }

    // T-SEC-BL08-10: SEC-BL08-04 - Multiple hooks execute in order, short-circuit on Deny
    [Fact]
    public async Task T_SEC_BL08_10_MultipleHooks_ExecuteInOrder_ShortCircuitOnDeny()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);

        var hook1 = new StubAuthHook("hook1", 1, (_, _) => ValueTask.FromResult(PluginAuthorizationResult.Deny("HOOK1_DENIED")));
        var hook2 = new StubAuthHook("hook2", 2, (_, _) => ValueTask.FromResult(PluginAuthorizationResult.Allow()));

        host.RegisterAuthorizationHook(hook1);
        host.RegisterAuthorizationHook(hook2);

        var policy = CreateAllowingPolicy(options, host);
        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        hook1.InvocationCount.ShouldBe(1);
        hook2.InvocationCount.ShouldBe(0); // Short-circuited!
    }

    // T-SEC-BL08-11: SEC-BL08-05 - ProhibitedRole ClusterAdmin is stripped and alert logged
    [Fact]
    public void T_SEC_BL08_11_ProhibitedRole_ClusterAdmin_IsStrippedAndAlertLogged()
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: ["ClusterAdmin", "GovernanceAdmin", "SecurityAdmin", "Analyst"]);

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

        var sanitized = guard.Sanitize("test-plugin", rawResult, context);

        sanitized.AddedRoles.ShouldNotContain("ClusterAdmin");
        sanitized.AddedRoles.ShouldNotContain("GovernanceAdmin");
        sanitized.AddedRoles.ShouldNotContain("SecurityAdmin");
        sanitized.AddedRoles.ShouldContain("Analyst");
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    // T-SEC-BL08-12: SEC-BL08-05 - Role name containing colon or control characters is stripped
    [Fact]
    public void T_SEC_BL08_12_RoleName_ContainingColonOrControlChars_IsStripped()
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: ["tenant:admin", "spoofed:role", "role\0null", "role\nnewline", "ValidRole"]);

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

        var sanitized = guard.Sanitize("test-plugin", rawResult, context);

        sanitized.AddedRoles.ShouldNotContain("tenant:admin");
        sanitized.AddedRoles.ShouldNotContain("spoofed:role");
        sanitized.AddedRoles.ShouldNotContain("role\0null");
        sanitized.AddedRoles.ShouldNotContain("role\nnewline");
        sanitized.AddedRoles.ShouldContain("ValidRole");
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    // T-SEC-BL08-13: SEC-BL08-06 - Reserved system SIDs are stripped and alert logged
    [Fact]
    public void T_SEC_BL08_13_ReservedSystemSids_AreStrippedAndAlertLogged()
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedGroupSids:
            [
                new Sid("S-1-5-18"),
                new Sid("S-1-5-19"),
                new Sid("S-1-5-20"),
                new Sid("S-1-5-32-544"),
                new Sid("S-1-5-32-545"),
                new Sid("S-1-5-21-100-200-300-1000")
            ]);

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

        var sanitized = guard.Sanitize("test-plugin", rawResult, context);

        sanitized.AddedGroupSids.Count.ShouldBe(1);
        sanitized.AddedGroupSids.First().Value.ShouldBe("S-1-5-21-100-200-300-1000");
    }

    // T-SEC-BL08-14: SEC-BL08-07 - Enriched roles and SIDs exceeding quota are clamped
    [Fact]
    public void T_SEC_BL08_14_EnrichedRolesAndSids_ExceedingQuota_AreClamped()
    {
        var options = new AuthPluginOptions
        {
            MaxEnrichedRolesPerCaller = 2,
            MaxEnrichedGroupSidsPerCaller = 3
        };
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var roles = new[] { "R1", "R2", "R3", "R4" };
        var sids = new[] { new Sid("S-1-5-21-1-1"), new Sid("S-1-5-21-1-2"), new Sid("S-1-5-21-1-3"), new Sid("S-1-5-21-1-4") };

        var context = new PluginSecurityContext
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

        var sanitized = guard.Sanitize("test-plugin", new PluginRoleEnrichmentResult(roles, sids), context);

        sanitized.AddedRoles.Count.ShouldBe(2);
        sanitized.AddedGroupSids.Count.ShouldBe(3);
    }

    // T-SEC-BL08-15: SEC-BL08-08 - Prior Casbin / Consent denial short-circuits, hook never invoked
    [Fact]
    public async Task T_SEC_BL08_15_PriorCasbinDenial_ShortCircuits_HookNeverInvoked()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);
        var hook = new StubAuthHook("hook_should_not_run", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Allow()));

        host.RegisterAuthorizationHook(hook);

        // Policy with DENY consent repository
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([
                new Consent
                {
                    TableIdentifier = new TableIdentifier("data_source", "public", "customers"),
                    TenantId = new TenantId("tenant-1"),
                    GranteeType = GranteeType.Role,
                    RoleName = "Analyst",
                    Effect = ConsentEffect.Deny,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1)
                }
            ]));

        var policy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null,
            options,
            NullMandatoryRowFilterResolver.Instance,
            authPluginHost: host);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        hook.InvocationCount.ShouldBe(0); // Invariant 1: NEVER invoked!
    }

    // T-SEC-BL08-16: SEC-BL08-09 - Authorization hook cannot unmask masked column
    [Fact]
    public async Task T_SEC_BL08_16_AuthorizationHook_CannotUnmaskMaskedColumn()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);

        // Hook attempts to modify column access
        var hook = new StubAuthHook("mask_bypass_attempt", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                deniedColumns: ["id"],
                hookName: "mask_bypass_attempt")));

        host.RegisterAuthorizationHook(hook);

        var columnRules = new[] { new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask } };
        var policy = CreateAllowingPolicy(options, host, columnRules);
        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        // Invariant 3: Hook can only lower access to Deny, never elevate from Mask/Deny to Clear
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Deny);
    }

    // T-SEC-BL08-17: SEC-BL08-10 - Additional row filter SQL is enclosed in parens AND (...) and validated
    [Fact]
    public async Task T_SEC_BL08_17_AdditionalRowFilterSql_EnclosedInParens_AndValidated()
    {
        var options = new GatewayOptions();
        var host = new AuthPluginHost(Options.Create(options), NullLogger<AuthPluginHost>.Instance);

        var hook = new StubAuthHook("filter_hook", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                additionalRowFilterSql: "region = 'APAC'",
                hookName: "filter_hook")));

        host.RegisterAuthorizationHook(hook);

        var policy = CreateAllowingPolicy(options, host);
        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("(region = 'APAC')");
    }

    // T-SEC-BL08-18: SEC-BL08-12 - PluginSecurityContext and PluginAuthorizationContext do not expose secrets
    [Fact]
    public void T_SEC_BL08_18_SecurityContext_DoesNotExposeBearerTokenOrSecrets()
    {
        var secContextProps = typeof(PluginSecurityContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var authContextProps = typeof(PluginAuthorizationContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        string[] forbiddenKeywords = ["token", "secret", "password", "key", "authorization", "bearer", "cookie", "header"];

        foreach (var prop in secContextProps)
        {
            foreach (var kw in forbiddenKeywords)
            {
                prop.Name.Contains(kw, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"PluginSecurityContext property '{prop.Name}' contains forbidden credential keyword '{kw}'.");
            }
        }

        foreach (var prop in authContextProps)
        {
            foreach (var kw in forbiddenKeywords)
            {
                prop.Name.Contains(kw, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"PluginAuthorizationContext property '{prop.Name}' contains forbidden credential keyword '{kw}'.");
            }
        }
    }

    private static TableAccessQuery CreateQuery() =>
        new(
            new Sid("user-1"),
            new TenantId("tenant-1"),
            new HashSet<Sid>(),
            new HashSet<string> { "Analyst" },
            new TableMetadata
            {
                Identifier = new TableIdentifier("data_source", "public", "customers"),
                Table = new Table { SourceName = "data_source", SchemaName = "public", TableName = "customers", SourceType = "PostgreSQL", DataSourceType = DataSourceType.Sql },
                Columns = [new TableColumn { ColumnName = "id", DataType = "INTEGER" }, new TableColumn { ColumnName = "ssn", DataType = "TEXT", IsSensitive = true }]
            },
            RequestedColumns: ["id", "ssn"]);

    private static TableAccessPolicy CreateAllowingPolicy(
        GatewayOptions options,
        AuthPluginHost host,
        IReadOnlyList<ConsentColumnRule>? columnRules = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([
                new Consent
                {
                    TableIdentifier = new TableIdentifier("data_source", "public", "customers"),
                    TenantId = new TenantId("tenant-1"),
                    GranteeType = GranteeType.Role,
                    RoleName = "Analyst",
                    Effect = ConsentEffect.Allow,
                    ColumnRules = columnRules ?? Array.Empty<ConsentColumnRule>(),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1)
                }
            ]));

        return new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null,
            options,
            NullMandatoryRowFilterResolver.Instance,
            authPluginHost: host);
    }
}
