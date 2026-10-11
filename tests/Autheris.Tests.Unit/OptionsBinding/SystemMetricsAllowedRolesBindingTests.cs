namespace Autheris.Tests.Unit.OptionsBinding;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>The binder appends to pre-filled lists; the system-metrics role default must not be merged with configuration.</summary>
public class SystemMetricsAllowedRolesBindingTests
{
    private static SystemMetricsOptions Bind(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return config.Get<SystemMetricsOptions>() ?? new SystemMetricsOptions();
    }

    [Fact]
    public void SingleConfiguredRole_IsNotMergedWithDefaults()
    {
        Bind(new() { ["AllowedRoles:0"] = "SecurityAdmin" })
            .GetEffectiveAllowedRoles().ToArray().ShouldBe(["SecurityAdmin"]);
    }

    [Fact]
    public void NothingConfigured_UsesDocumentedDefault()
    {
        Bind(new() { ["Enabled"] = "true" })
            .GetEffectiveAllowedRoles().ToArray().ShouldBe(["GovernanceAdmin", "ClusterAdmin", "SecurityAdmin"]);
    }

    [Fact]
    public void ProgrammaticEmptyList_StaysEmpty()
    {
        new SystemMetricsOptions { AllowedRoles = [] }.GetEffectiveAllowedRoles().ShouldBeEmpty();
    }
}
