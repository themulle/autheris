namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #447 (SEC-ATR-01, SEC-ATR-08, SEC-ATR-09): the single fail-closed transport gate.</summary>
[Trait("Category", "Security")]
public sealed class DataSourceTransportGateTests
{
    public static TheoryData<DataSourceTransportFlags> SingleFlags => new()
    {
        DataSourceTransportFlags.WebSql, DataSourceTransportFlags.GraphQL, DataSourceTransportFlags.Rest, DataSourceTransportFlags.OData,
        DataSourceTransportFlags.Mcp, DataSourceTransportFlags.Trino, DataSourceTransportFlags.ArrowFlight
    };

    private static HierarchicalDataSourceOptionsResolver NewResolver(params (string Name, DataSourceTransportFlags? Flags)[] profiles) =>
        NewResolver(null, profiles);

    private static HierarchicalDataSourceOptionsResolver NewResolver(WebSqlOptions? webSql, params (string Name, DataSourceTransportFlags? Flags)[] profiles) =>
        new(
            new FixedMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
            {
                Profiles = profiles.ToDictionary(p => p.Name, p => new DataSourceProfileOptions { AllowedTransports = p.Flags }, StringComparer.OrdinalIgnoreCase)
            }),
            new FixedMonitor<GatewayOptions>(new GatewayOptions { WebSql = webSql ?? new WebSqlOptions() }));

    [Theory]
    [MemberData(nameof(SingleFlags))]
    public void Decide_AllowsWhenFlagSet(DataSourceTransportFlags flag)
    {
        using var resolver = NewResolver(("src", flag));
        DataSourceTransportGate.Decide(resolver, "src", flag).Reason.ShouldBe(TransportDecisionReason.Allowed);
    }

    [Fact]
    public void Decide_DeniesWhenFlagMissing()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.GraphQL));
        DataSourceTransportGate.Decide(resolver, "src", DataSourceTransportFlags.WebSql).Reason.ShouldBe(TransportDecisionReason.FlagNotSet);
    }

    [Fact]
    public void Decide_DeniesUnknownSource()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.All));
        DataSourceTransportGate.Decide(resolver, "other", DataSourceTransportFlags.WebSql).Reason.ShouldBe(TransportDecisionReason.UnknownSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_DeniesBlankName(string? name)
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.All));
        DataSourceTransportGate.Decide(resolver, name, DataSourceTransportFlags.WebSql).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Decide_DeniesTransportNone()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.All));
        DataSourceTransportGate.Decide(resolver, "src", DataSourceTransportFlags.None).Reason.ShouldBe(TransportDecisionReason.TransportUnset);
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.WebSql | DataSourceTransportFlags.GraphQL)]
    [InlineData(DataSourceTransportFlags.All)]
    [InlineData((DataSourceTransportFlags)(1 << 20))]
    public void Decide_DeniesMultiBitTransport(DataSourceTransportFlags transport)
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.All));
        DataSourceTransportGate.Decide(resolver, "src", transport).Reason.ShouldBe(TransportDecisionReason.InvalidTransport);
    }

    [Fact]
    public void Decide_DeniesWhenResolverThrows()
    {
        var resolver = Substitute.For<IDataSourceOptionsResolver>();
        resolver.TryResolve(Arg.Any<string?>(), out Arg.Any<Autheris.Domain.Options.DataSources.EffectiveDataSourceProfile>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        DataSourceTransportGate.Decide(resolver, "src", DataSourceTransportFlags.WebSql).Reason.ShouldBe(TransportDecisionReason.EvaluationFailed);
    }

    [Fact]
    public async Task Gate_ResolverThrows_Denies()
    {
        var resolver = Substitute.For<IDataSourceOptionsResolver>();
        resolver.TryResolve(Arg.Any<string?>(), out Arg.Any<Autheris.Domain.Options.DataSources.EffectiveDataSourceProfile>())
            .Returns(_ => throw new InvalidOperationException("boom"));
        resolver.CanonicalName(Arg.Any<string?>()).Returns(call => call.Arg<string?>());
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("src"));
        rig.Denials.Count.ShouldBe(1);
        rig.Denials[0].Reason.ShouldBe(TransportDecisionReason.EvaluationFailed);
    }

    [Fact]
    public async Task Ensure_UnknownAndDeniedThrowSameShape()
    {
        using var resolver = NewResolver(("known", DataSourceTransportFlags.GraphQL));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        var denied = await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("known"));
        var unknown = await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("known2"));

        denied.ErrorCode.ShouldBe("TRANSPORT_NOT_ALLOWED");
        unknown.ErrorCode.ShouldBe(denied.ErrorCode);
        unknown.GetType().ShouldBe(denied.GetType());
        unknown.Message.Replace("known2", "X").ShouldBe(denied.Message.Replace("known", "X"));
        rig.Denials.Select(d => d.Reason).ShouldBe([TransportDecisionReason.FlagNotSet, TransportDecisionReason.UnknownSource]);
    }

    [Fact]
    public async Task Gate_UnknownBlankMappedPhysical_SameShapeAsDenied()
    {
        using var resolver = NewResolver(Mapping("alias", "physical"), ("physical", DataSourceTransportFlags.GraphQL));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        foreach (var name in new string?[] { "alias", "physical", "nope", "", null })
        {
            var ex = await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync(name));
            ex.ErrorCode.ShouldBe("TRANSPORT_NOT_ALLOWED");
        }
    }

    [Fact]
    public async Task EnsureAll_OneDeniedDeniesAll_NoPartialAudit()
    {
        using var resolver = NewResolver(("s", DataSourceTransportFlags.GraphQL), ("t", DataSourceTransportFlags.All));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAllAsync(["t", "s", "t"]));

        rig.Denials.Count.ShouldBe(1);
        rig.Denials[0].LogicalName.ShouldBe("s");
        await Should.NotThrowAsync(() => rig.Gate.EnsureAllAsync(["t"]));
        rig.Denials.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ensure_CanonicalisesMappedName()
    {
        using var resolver = NewResolver(Mapping("alias", "physical"), ("physical", DataSourceTransportFlags.GraphQL), ("alias", DataSourceTransportFlags.All));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        // the mapped (physical) profile decides, not the profile that happens to carry the logical name
        await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("alias"));
        rig.Denials[0].CanonicalName.ShouldBe("physical");
    }

    [Fact]
    public async Task Ensure_MessageNamesLogicalNotPhysicalSource()
    {
        using var resolver = NewResolver(Mapping("sales", "secret_physical_key"), ("secret_physical_key", DataSourceTransportFlags.GraphQL));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        var ex = await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("sales"));

        ex.Message.ShouldBe("Access to data source 'sales' is not allowed over transport 'WebSql'.");
        ex.Message.ShouldNotContain("secret_physical_key");
    }

    [Fact]
    public async Task Gate_ControlCharsAndLongName_Sanitised()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.GraphQL));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);
        var evil = "a\r\nb\u0000" + new string('x', 500);

        var ex = await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync(evil));

        ex.Message.ShouldNotContain("\r");
        ex.Message.ShouldNotContain("\n");
        ex.Message.ShouldNotContain("\0");
        ex.Message.Length.ShouldBeLessThan(260);
        TransportNotAllowedException.SanitizeName(evil).Length.ShouldBe(TransportNotAllowedException.MaxNameLength);
    }

    [Fact]
    public async Task Ensure_UnsetIngress_Denies()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.All));
        var rig = new Rig(resolver, DataSourceTransportFlags.None);

        await Should.ThrowAsync<TransportNotAllowedException>(() => rig.Gate.EnsureAsync("src"));
        rig.Denials[0].Reason.ShouldBe(TransportDecisionReason.TransportUnset);
        rig.Gate.IsAllowed("src").ShouldBeFalse();
    }

    [Fact]
    public async Task Ensure_AllowedTransport_DoesNotAuditOrThrow()
    {
        using var resolver = NewResolver(("src", DataSourceTransportFlags.WebSql));
        var rig = new Rig(resolver, DataSourceTransportFlags.WebSql);

        await Should.NotThrowAsync(() => rig.Gate.EnsureAsync("src"));
        rig.Gate.IsAllowed("src").ShouldBeTrue();
        rig.Denials.ShouldBeEmpty();
    }

    private static WebSqlOptions Mapping(string logical, string physical) => new()
    {
        DataSourceMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [logical] = physical }
    };

    private sealed class Rig
    {
        public Rig(IDataSourceOptionsResolver resolver, DataSourceTransportFlags ingress)
        {
            var context = new IngressTransportContext();
            if (ingress != DataSourceTransportFlags.None)
            {
                context.Set(ingress);
            }

            Gate = new DataSourceTransportGate(context, resolver, new CapturingRecorder(Denials));
        }

        public List<TransportDenial> Denials { get; } = [];

        public DataSourceTransportGate Gate { get; }
    }

    private sealed class CapturingRecorder(List<TransportDenial> sink) : ITransportDenialRecorder
    {
        public Task RecordDenialAsync(TransportDenial denial, CancellationToken ct = default)
        {
            sink.Add(denial);
            return Task.CompletedTask;
        }
    }

    internal sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
