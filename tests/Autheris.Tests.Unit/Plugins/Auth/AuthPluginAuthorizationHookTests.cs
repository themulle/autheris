namespace Autheris.Tests.Unit.Plugins.Auth;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Autheris.Infrastructure.Plugins.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AuthPluginAuthorizationHookTests
{
    private static readonly TenantId TestTenant = new("tenant-beta");
    private static readonly TableIdentifier TestTable = new("data_source", "public", "customers");

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

    private static TableMetadata CreateMetadata() => new()
    {
        Identifier = TestTable,
        Table = new Table
        {
            SourceName = "data_source",
            SchemaName = "public",
            TableName = "customers",
            SourceType = "PostgreSQL",
            DataSourceType = DataSourceType.Sql,
            Tags = ["PII", "ExportControlled"]
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { ColumnName = "name", DataType = "TEXT" },
            new TableColumn { ColumnName = "ssn", DataType = "TEXT" },
            new TableColumn { ColumnName = "region", DataType = "TEXT" }
        ]
    };

    private static TableAccessQuery CreateQuery() =>
        new(
            new Sid("user-1"),
            TestTenant,
            new HashSet<Sid>(),
            new HashSet<string> { "Analyst" },
            CreateMetadata(),
            RequestedColumns: ["id", "name", "ssn", "region"]);

    private static (TableAccessPolicy Policy, AuthPluginHost Host, FakeTimeProvider TimeProvider) CreateTestPolicy(
        GatewayOptions? options = null)
    {
        var opts = options ?? new GatewayOptions();
        var timeProvider = new FakeTimeProvider();
        var host = new AuthPluginHost(Options.Create(opts), NullLogger<AuthPluginHost>.Instance, timeProvider: timeProvider);

        var consentRepo = Substitute.For<IConsentRepository>();
        // Return an allow consent so baseline passes
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([
                new Consent
                {
                    TableIdentifier = TestTable,
                    TenantId = TestTenant,
                    GranteeType = GranteeType.Role,
                    RoleName = "Analyst",
                    Effect = ConsentEffect.Allow,
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
            opts,
            NullMandatoryRowFilterResolver.Instance,
            authPluginHost: host);

        return (policy, host, timeProvider);
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookAllows_TableAccessDecisionAllowed()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook = new StubAuthHook("allow_hook", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Allow("allow_hook")));
        host.RegisterAuthorizationHook(hook);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        hook.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookDenies_TableAccessDecisionDeniedWithReason()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook = new StubAuthHook("compliance_fence", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Deny("GEO_FENCE_DENIAL", "Access from foreign jurisdiction denied.", "compliance_fence")));
        host.RegisterAuthorizationHook(hook);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("GEO_FENCE_DENIAL"));
        hook.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookRestricts_RemovesColumnsAndAddsRowFilter()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook = new StubAuthHook("data_minimizer", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                deniedColumns: ["ssn"],
                additionalRowFilterSql: "region = 'US'",
                hookName: "data_minimizer")));
        host.RegisterAuthorizationHook(hook);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("region = 'US'");
        hook.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenMultipleHooks_ExecuteInAscendingOrder()
    {
        var (policy, host, _) = CreateTestPolicy();
        var executionOrder = new List<string>();

        var hook10 = new StubAuthHook("hook_second", 10, (_, _) =>
        {
            executionOrder.Add("hook_second");
            return ValueTask.FromResult(PluginAuthorizationResult.Allow());
        });

        var hook5 = new StubAuthHook("hook_first", 5, (_, _) =>
        {
            executionOrder.Add("hook_first");
            return ValueTask.FromResult(PluginAuthorizationResult.Allow());
        });

        host.RegisterAuthorizationHook(hook10);
        host.RegisterAuthorizationHook(hook5);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        executionOrder.ShouldBe(["hook_first", "hook_second"]);
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenFirstHookDenies_SubsequentHooksNeverInvoked()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook1 = new StubAuthHook("first_deny", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Deny("STOP_HERE")));

        var hook2 = new StubAuthHook("second_never_called", 2, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Allow()));

        host.RegisterAuthorizationHook(hook1);
        host.RegisterAuthorizationHook(hook2);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        hook1.InvocationCount.ShouldBe(1);
        hook2.InvocationCount.ShouldBe(0); // Short-circuited!
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenMultipleHooksRestrict_CombinesRestrictionsMonotonically()
    {
        var (policy, host, _) = CreateTestPolicy();

        var hook1 = new StubAuthHook("hook1", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                deniedColumns: ["ssn"],
                additionalRowFilterSql: "region = 'EU'")));

        var hook2 = new StubAuthHook("hook2", 2, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                deniedColumns: ["name"],
                additionalRowFilterSql: "id > 100")));

        host.RegisterAuthorizationHook(hook1);
        host.RegisterAuthorizationHook(hook2);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Deny);
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("region = 'EU'");
        decision.CombinedRowFilterSql.ShouldContain("id > 100");
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookThrows_FailsClosedToDeny()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook = new StubAuthHook("crashing_hook", 1, (_, _) =>
            throw new InvalidOperationException("External service crashed"));

        host.RegisterAuthorizationHook(hook);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("AUTH_PLUGIN_FAILED"));
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookTimesOut_FailsClosedToTimeout()
    {
        var (policy, host, timeProvider) = CreateTestPolicy();
        var hook = new StubAuthHook("hanging_hook", 1, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return PluginAuthorizationResult.Allow();
        });

        host.RegisterAuthorizationHook(hook);

        var decideTask = policy.DecideAsync(CreateQuery(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var decision = await decideTask;
        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("AUTH_PLUGIN_TIMEOUT"));
    }

    [Fact]
    public async Task EvaluateAccessAsync_WhenHookFilterSqlIsInvalid_FailsClosedWithInvalidFilter()
    {
        var (policy, host, _) = CreateTestPolicy();
        var hook = new StubAuthHook("injection_attempt_hook", 1, (_, _) =>
            ValueTask.FromResult(PluginAuthorizationResult.Restrict(
                additionalRowFilterSql: "1 = 1; DROP TABLE customers; --")));

        host.RegisterAuthorizationHook(hook);

        var decision = await policy.DecideAsync(CreateQuery(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("AUTH_HOOK_INVALID_FILTER"));
    }
}
