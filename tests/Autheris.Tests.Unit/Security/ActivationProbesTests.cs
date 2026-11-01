using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class ActivationProbesTests
{
    public static IEnumerable<object[]> ProbeNames() =>
        ActivationProbes.Build().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(ProbeNames))]
    public void Probe_Passes_UnderUnmutatedCode(string name)
    {
        var probe = ActivationProbes.Build().Single(p => p.Name == name);
        probe.Run();
    }

    [Fact]
    public void SessionPin_Fails_WhenSessionPinReadBackDisabled()
    {
        var probe = ActivationProbes.Build().Single(p => p.Name == "SessionPin_UnverifiedOrFailedPin_FailsClosed");
        using var _ = CompilerHooks.Use(new TestMutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.SessionPinReadBack } });
        Assert.ThrowsAny<Exception>(() => probe.Run());
    }

    [Fact]
    public void ErrorMapper_Fails_WhenErrorMapperFixedMessageDisabled()
    {
        var probe = ActivationProbes.Build().Single(p => p.Name == "ErrorMapper_ReturnsTheFixedMessage_NeverTheExceptionMessage");
        using var _ = CompilerHooks.Use(new TestMutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.ErrorMapperFixedMessage } });
        Assert.ThrowsAny<Exception>(() => probe.Run());
    }

    [Fact]
    public void TenantRegistry_Fails_WhenTenantRegistryFailClosedDisabled()
    {
        var probe = ActivationProbes.Build().Single(p => p.Name == "TenantRegistryGate_StoreUnavailable_Throws_NeverRegistered");
        using var _ = CompilerHooks.Use(new TestMutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.TenantRegistryFailClosed } });
        Assert.ThrowsAny<Exception>(() => probe.Run());
    }

    [Fact]
    public void CatalogLoadFailure_Fails_WhenCatalogLoadFailureDeniesDisabled()
    {
        var probe = ActivationProbes.Build().Single(p => p.Name == "Catalog_LoadFailure_DeniesTheTable");
        using var _ = CompilerHooks.Use(new TestMutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.CatalogLoadFailureDenies } });
        Assert.ThrowsAny<Exception>(() => probe.Run());
    }

    private sealed class TestMutantHooks : ICompilerHooks
    {
        public HashSet<SecurityControl>? DisabledControls { get; init; }
        public bool IsDisabled(SecurityControl control) => DisabledControls?.Contains(control) == true;
        public void OnPass(string name) { }
        public TrinoSqlEngine.Ast.Nodes.SqlStatement InterceptInjected(TrinoSqlEngine.Ast.Nodes.SqlStatement secured) => secured;
        public string InterceptEmitted(string sql) => sql;
        public bool OmitsCacheKeyField(string name) => false;
        public DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested) => requested;
        public bool BypassesExecutionStackGuard() => false;
        public TrinoSqlEngine.Ast.Emit.GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect) => null;
    }
}
