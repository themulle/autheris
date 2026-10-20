namespace Autheris.Tests.Unit.Plugins.Auth;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Infrastructure.Plugins.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class AuthPluginCallerAttributeTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly List<CallerAttributeResolver> _resolvers = [];

    public void Dispose() => _resolvers.ForEach(r => r.Dispose());

    private sealed class StubPluginCallerAttributeProvider(string kind, Func<CallerSubject, CallerAttributeDefinition, CancellationToken, Task<CallerAttributeResult>> behavior)
        : IPluginCallerAttributeProvider
    {
        public int InvocationCount;
        public string Kind => kind;

        public async ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            Interlocked.Increment(ref InvocationCount);
            return await behavior(subject, definition, ct);
        }
    }

    private static CallerAttributeDefinition CreateDefinition(string name, string kind, int ttlSeconds = 60) =>
        new(
            Guid.NewGuid(),
            new TenantId("tenant-1"),
            name,
            CallerAttributeType.Text,
            CallerAttributeCardinality.List,
            MaxValues: 50,
            TtlSeconds: ttlSeconds,
            new CallerAttributeProviderBinding(kind, new Dictionary<string, string>()),
            null,
            FilterApprovalStatus.Active);

    private static CallerSubject CreateSubject(string sub = "11111111-1111-1111-1111-111111111111")
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("oid", sub, ClaimValueTypes.String, "https://login.example/tenant/v2.0"));
        var principal = new ClaimsPrincipal(identity);
        var options = new CallerAttributeOptions
        {
            Enabled = true,
            Claims = new CallerAttributeClaimsOptions
            {
                TrustedIssuers = [new CallerAttributeTrustedIssuer { Issuer = "https://login.example/tenant/v2.0", SubjectKeyIsPerson = true }]
            }
        };
        return CallerSubjectFactory.Create(principal, new TenantId("tenant-1"), options);
    }

    private static GatewayOptions CreateGatewayOptions(bool authPluginsEnabled = true) => new()
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
            Auth = new AuthPluginOptions { Enabled = authPluginsEnabled }
        }
    };

    [Fact]
    public async Task ResolveAsync_WhenPluginProviderRegistered_ResolvesCustomAttribute()
    {
        var gatewayOptions = CreateGatewayOptions();
        var host = new AuthPluginHost(Options.Create(gatewayOptions), NullLogger<AuthPluginHost>.Instance, timeProvider: _time);

        var pluginProvider = new StubPluginCallerAttributeProvider("custom_ldap", (_, _, _) =>
            Task.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue(["ENG", "R&D"], SqlParameterType.String))));

        host.RegisterCallerAttributeProvider(pluginProvider);

        var def = CreateDefinition("department", "custom_ldap");
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        var resolver = new CallerAttributeResolver(
            registry,
            [],
            Options.Create(gatewayOptions),
            _time,
            NullLogger<CallerAttributeResolver>.Instance,
            host);
        _resolvers.Add(resolver);

        var subject = CreateSubject();
        var result = await resolver.ResolveAsync(subject, ["department"], CancellationToken.None);

        result.TryGet("department", out var attrResult).ShouldBeTrue();
        attrResult.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        attrResult.Value.ShouldNotBeNull();
        attrResult.Value.Count.ShouldBe(2);
        attrResult.Value[0].ShouldBe("ENG");
        attrResult.Value[1].ShouldBe("R&D");
        pluginProvider.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenPluginProviderReturnsValue_ParticipatesInCaching()
    {
        var gatewayOptions = CreateGatewayOptions();
        var host = new AuthPluginHost(Options.Create(gatewayOptions), NullLogger<AuthPluginHost>.Instance, timeProvider: _time);

        var pluginProvider = new StubPluginCallerAttributeProvider("custom_hr", (_, _, _) =>
            Task.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue(["Level5"], SqlParameterType.String))));

        host.RegisterCallerAttributeProvider(pluginProvider);

        var def = CreateDefinition("clearance_level", "custom_hr", ttlSeconds: 120);
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        var resolver = new CallerAttributeResolver(
            registry,
            [],
            Options.Create(gatewayOptions),
            _time,
            NullLogger<CallerAttributeResolver>.Instance,
            host);
        _resolvers.Add(resolver);

        var subject = CreateSubject();

        // First resolution: hits provider
        var res1 = await resolver.ResolveAsync(subject, ["clearance_level"], CancellationToken.None);
        res1.TryGet("clearance_level", out var val1).ShouldBeTrue();
        val1.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        pluginProvider.InvocationCount.ShouldBe(1);

        // Second resolution within TTL: cache hit
        var res2 = await resolver.ResolveAsync(subject, ["clearance_level"], CancellationToken.None);
        res2.TryGet("clearance_level", out var val2).ShouldBeTrue();
        val2.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        pluginProvider.InvocationCount.ShouldBe(1); // Provider not called second time!
    }

    [Fact]
    public async Task ResolveAsync_WhenPluginProviderThrows_FailsClosedToFailedOutcome()
    {
        var gatewayOptions = CreateGatewayOptions();
        var host = new AuthPluginHost(Options.Create(gatewayOptions), NullLogger<AuthPluginHost>.Instance, timeProvider: _time);

        var pluginProvider = new StubPluginCallerAttributeProvider("throwing_ldap", (_, _, _) =>
            throw new InvalidOperationException("LDAP connection failure"));

        host.RegisterCallerAttributeProvider(pluginProvider);

        var def = CreateDefinition("ldap_group", "throwing_ldap");
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        var resolver = new CallerAttributeResolver(
            registry,
            [],
            Options.Create(gatewayOptions),
            _time,
            NullLogger<CallerAttributeResolver>.Instance,
            host);
        _resolvers.Add(resolver);

        var subject = CreateSubject();
        var result = await resolver.ResolveAsync(subject, ["ldap_group"], CancellationToken.None);

        result.TryGet("ldap_group", out var attrResult).ShouldBeTrue();
        attrResult.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        attrResult.ReasonCode.ShouldBe("SourceUnavailable");
    }

    [Fact]
    public async Task ResolveAsync_WhenPluginProviderKindNotRegistered_ReturnsProviderUnavailable()
    {
        var gatewayOptions = CreateGatewayOptions();
        var host = new AuthPluginHost(Options.Create(gatewayOptions), NullLogger<AuthPluginHost>.Instance, timeProvider: _time);

        var def = CreateDefinition("missing_attr", "nonexistent_plugin");
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        var resolver = new CallerAttributeResolver(
            registry,
            [],
            Options.Create(gatewayOptions),
            _time,
            NullLogger<CallerAttributeResolver>.Instance,
            host);
        _resolvers.Add(resolver);

        var subject = CreateSubject();
        var result = await resolver.ResolveAsync(subject, ["missing_attr"], CancellationToken.None);

        result.TryGet("missing_attr", out var attrResult).ShouldBeTrue();
        attrResult.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        attrResult.ReasonCode.ShouldBe("ProviderUnavailable");
    }

    [Fact]
    public async Task ResolveAsync_WhenAuthPluginsDisabled_DoesNotResolvePluginAttribute()
    {
        var gatewayOptions = CreateGatewayOptions(authPluginsEnabled: false);
        var host = new AuthPluginHost(Options.Create(gatewayOptions), NullLogger<AuthPluginHost>.Instance, timeProvider: _time);

        var pluginProvider = new StubPluginCallerAttributeProvider("disabled_plugin", (_, _, _) =>
            Task.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue(["val"], SqlParameterType.String))));

        host.RegisterCallerAttributeProvider(pluginProvider);

        var def = CreateDefinition("test_attr", "disabled_plugin");
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));

        var resolver = new CallerAttributeResolver(
            registry,
            [],
            Options.Create(gatewayOptions),
            _time,
            NullLogger<CallerAttributeResolver>.Instance,
            host);
        _resolvers.Add(resolver);

        var subject = CreateSubject();
        var result = await resolver.ResolveAsync(subject, ["test_attr"], CancellationToken.None);

        result.TryGet("test_attr", out var attrResult).ShouldBeTrue();
        attrResult.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        attrResult.ReasonCode.ShouldBe("ProviderUnavailable");
        pluginProvider.InvocationCount.ShouldBe(0);
    }
}
