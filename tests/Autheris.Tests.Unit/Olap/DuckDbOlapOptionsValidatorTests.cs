namespace Autheris.Tests.Unit.Olap;

using System;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MISC-05: the OLAP memory configuration must fit the container (fail closed outside Development).</summary>
public sealed class DuckDbOlapOptionsValidatorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static Func<string, string?> Cgroup(string? v2Max) => path => path.EndsWith("memory.max", StringComparison.Ordinal) ? v2Max : null;

    private static bool Valid(DuckDbOlapOptions olap, string? cgroupMax, string environment = "Production") =>
        new DuckDbOlapOptionsValidator(Env(environment), null, Cgroup(cgroupMax))
            .Validate(null, new GatewayOptions { DuckDbOlap = olap }).Succeeded;

    [Fact]
    public void Defaults_FitHelmLimit2GiB() => Valid(new DuckDbOlapOptions(), (2 * GiB).ToString()).ShouldBeTrue();

    [Fact]
    public void Defaults_FitCompose2GiB() => Valid(new DuckDbOlapOptions(), (2 * GiB).ToString()).ShouldBeTrue();

    [Fact]
    public void OverCommit_FailsValidation() =>
        Valid(new DuckDbOlapOptions { MaxMemory = "1GB", MaxConcurrentQueries = 4 }, (2 * GiB).ToString()).ShouldBeFalse();

    [Fact]
    public void UnparseableMaxMemory_Fails() =>
        Valid(new DuckDbOlapOptions { MaxMemory = "lots" }, (2 * GiB).ToString()).ShouldBeFalse();

    [Fact]
    public void NoLimitNoBudget_Production_Fails() => Valid(new DuckDbOlapOptions(), null).ShouldBeFalse();

    [Fact]
    public void NoLimitNoBudget_Development_Passes() => Valid(new DuckDbOlapOptions(), null, "Development").ShouldBeTrue();

    [Fact]
    public void CgroupV2Max_TreatedAsNoLimit()
    {
        ContainerMemoryLimit.Read(Cgroup("max")).ShouldBeNull();
        Valid(new DuckDbOlapOptions(), "max").ShouldBeFalse();
    }

    [Fact]
    public void ExplicitBudget_OverridesCgroup() =>
        Valid(new DuckDbOlapOptions { MemoryBudgetBytes = 4 * GiB }, (1 * GiB).ToString()).ShouldBeTrue();

    [Fact]
    public void Disabled_IsNotChecked() => Valid(new DuckDbOlapOptions { Enabled = false, MaxMemory = "lots" }, null).ShouldBeTrue();
}
