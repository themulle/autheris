#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting.Warmup;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using HotChocolate.Execution;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #1109: the executor gate builds in the background, so the host start never waits for the schema build.</summary>
[Trait("Category", "Security")]
public sealed class GraphQlExecutorGateStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StartAsync_ReturnsBeforeBuild_PendingUntilBuilt_LogsStartAndDuration()
    {
        var executor = Substitute.For<IRequestExecutor>();
        var proof = new GraphQlWarmupProof();
        proof.Record(executor);
        var build = new TaskCompletionSource<IRequestExecutor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IRequestExecutor>(build.Task));
        var logger = new CapturingLogger<GraphQlExecutorGate>();
        using var gate = new GraphQlExecutorGate(
            provider, Options.Create(new GatewayOptions()), new FakeTimeProvider(), logger, proof,
            warmupOptions: Options.Create(new StartupWarmupOptions()));

        // The host start returns at once; readiness stays Pending (Warming) until the build finishes.
        await gate.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await WaitHelper.WaitUntil(() => logger.Entries.Count > 0, Bound);
        logger.Messages[0].ShouldContain("build started");
        gate.State.ShouldBe(ExecutorGateState.Pending);
        gate.ExecuteTask!.IsCompleted.ShouldBeFalse();

        build.SetResult(executor);
        await gate.ExecuteTask.WaitAsync(Bound);

        gate.State.ShouldBe(ExecutorGateState.Ready);
        logger.Messages.ShouldContain(m => m.StartsWith("GraphQL executor ready after", StringComparison.Ordinal));
    }
}
