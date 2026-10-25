using Autheris.Sim.ApiHost;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Unit tests of the single bounded shutdown path of the sim host (REQ-LB-01..03). Real short timeouts, no host.</summary>
public sealed class SimHostShutdownTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-05
    public async Task Shutdown_StoppedAndDisposed_ReturnsZeroAndCleansUp()
    {
        var cleaned = 0;
        var fallback = 0;
        var stderr = new StringWriter();

        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => true, () => { fallback++; return Task.CompletedTask; }, () => cleaned++, stderr, Short, Short);

        code.ShouldBe(0);
        cleaned.ShouldBe(1);
        fallback.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-06
    public void Shutdown_RequestTwice_StopsOnce()
    {
        var shutdown = new SimHostShutdown();

        shutdown.Request().ShouldBeTrue();
        shutdown.Request().ShouldBeFalse();
        shutdown.Request().ShouldBeFalse();
        shutdown.Requested.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-05, SEC-LB-06
    public async Task Shutdown_StopNeverSignalled_FallsBackToDispose_Returns5()
    {
        var cleaned = 0;
        var fallback = 0;

        var code = await new SimHostShutdown().RunAsync(ct => Task.Delay(Timeout.Infinite, ct), () => true, () => { fallback++; return Task.CompletedTask; }, () => cleaned++, new StringWriter(), Short, Short);

        code.ShouldBe(SimHostShutdown.ExitShutdownTimeout);
        fallback.ShouldBe(1);
        cleaned.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-05, SEC-LB-06
    public async Task Shutdown_HostNeverDisposed_FallsBackToDispose_Returns5()
    {
        var cleaned = 0;
        var fallback = 0;

        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => false, () => { fallback++; return Task.CompletedTask; }, () => cleaned++, new StringWriter(), Short, Short);

        code.ShouldBe(5);
        fallback.ShouldBe(1);
        cleaned.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-06
    public async Task Shutdown_FallbackDisposeHangs_IsBoundedAndReturns4()
    {
        var cleaned = 0;

        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => false, () => new TaskCompletionSource().Task, () => cleaned++, new StringWriter(), Short, Short);

        code.ShouldBe(SimHostShutdown.ExitShutdownFailed);
        cleaned.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-03, SEC-LB-05
    public async Task Shutdown_FallbackDisposeThrows_Returns4_WritesTypeAndMessage_CleansUp()
    {
        var cleaned = 0;
        var stderr = new StringWriter();

        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => false, () => throw new ObjectDisposedException("root", "boom\nline2"), () => cleaned++, stderr, Short, Short);

        code.ShouldBe(4);
        cleaned.ShouldBe(1);
        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain(l => l.StartsWith("sim-api-host: shutdown failed: ObjectDisposedException: ", StringComparison.Ordinal) && l.Contains("boom line2", StringComparison.Ordinal));
        lines.ShouldNotContain(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-05
    public async Task Shutdown_WaitStoppedThrows_Returns4_CleansUp()
    {
        var cleaned = 0;

        var code = await new SimHostShutdown().RunAsync(_ => throw new InvalidOperationException("x"), () => true, () => Task.CompletedTask, () => cleaned++, new StringWriter(), Short, Short);

        code.ShouldBe(4);
        cleaned.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02
    public async Task Shutdown_CleanupRunsEvenWhenStderrWriteFails()
    {
        var cleaned = 0;

        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => false, () => throw new InvalidOperationException("x"), () => cleaned++, new ThrowingWriter(), Short, Short);

        code.ShouldBe(4);
        cleaned.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-05
    public async Task Shutdown_CleanupThrows_NeverReturnsZero()
    {
        var code = await new SimHostShutdown().RunAsync(_ => Task.CompletedTask, () => true, () => Task.CompletedTask, () => throw new IOException("wipe"), new StringWriter(), Short, Short);

        code.ShouldBe(4);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr closed");
    }
}
