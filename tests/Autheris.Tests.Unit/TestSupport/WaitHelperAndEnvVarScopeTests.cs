using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.TestSupport;

[Collection("EnvVarScopeSelfTests")]
public sealed class WaitHelperAndEnvVarScopeTests
{
    private const string Var = "AUTHERIS_TEST_ENVVARSCOPE_SELFTEST";

    [Fact]
    public async Task WaitUntil_ReturnsOnceThePredicateTurnsTrue()
    {
        var flag = false;
        _ = Task.Run(async () => { await Task.Yield(); flag = true; });

        await WaitHelper.WaitUntil(() => Volatile.Read(ref flag), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WaitUntil_AsyncPredicate_ReturnsOnceTheTaskPredicateTurnsTrue()
    {
        var calls = 0;

        await WaitHelper.WaitUntil(() => Task.FromResult(++calls >= 3), TimeSpan.FromSeconds(30), pollInterval: TimeSpan.FromMilliseconds(1));

        calls.ShouldBe(3);
    }

    [Fact]
    public async Task WaitUntil_Timeout_FailsWithAClearMessage()
    {
        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            WaitHelper.WaitUntil(() => false, TimeSpan.FromMilliseconds(50), "the worker never started"));

        ex.Message.ShouldContain("not met within");
        ex.Message.ShouldContain("the worker never started");
    }

    [Fact]
    public async Task RunGuarded_ReturnsTheResultOfTheWork()
    {
        (await WaitHelper.RunGuarded(() => 42)).ShouldBe(42);
    }

    [Fact]
    public void EnvVarScope_SetsTheValueAndRestoresThePreviousOneOnDispose()
    {
        Environment.SetEnvironmentVariable(Var, "before");
        try
        {
            using (var scope = new EnvVarScope(Var, "during"))
            {
                Environment.GetEnvironmentVariable(Var).ShouldBe("during");
                scope.Set(Var, "again");
                Environment.GetEnvironmentVariable(Var).ShouldBe("again");
            }

            Environment.GetEnvironmentVariable(Var).ShouldBe("before");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, null);
        }
    }

    [Fact]
    public void EnvVarScope_SetTwiceOnTheSameName_RestoresTheOriginalValue()
    {
        Environment.SetEnvironmentVariable(Var, "original");
        try
        {
            using (var scope = new EnvVarScope(Var, "first"))
            {
                scope.Set(Var, "second");
            }

            Environment.GetEnvironmentVariable(Var).ShouldBe("original");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, null);
        }
    }

    [Fact]
    public void EnvVarScope_RestoresAnUnsetVariableToUnset()
    {
        Environment.SetEnvironmentVariable(Var, null);

        using (new EnvVarScope((Var, "temporary")))
        {
            Environment.GetEnvironmentVariable(Var).ShouldBe("temporary");
        }

        Environment.GetEnvironmentVariable(Var).ShouldBeNull();
    }
}
