using Autheris.Application.Extensibility;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Extensibility;

[Trait("Category", "Security")]
public sealed class ExtensibilityPipelineFailurePolicyTests
{
    private sealed class ThrowingIngress : IIngressInterceptor
    {
        public ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class HangingIngress : IIngressInterceptor
    {
        public async ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return IngressResult.Continue();
        }
    }

    private sealed class ThrowingEgress : IEgressInterceptor
    {
        public ValueTask<EgressResult> OnEgressAsync(EgressContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class MarkerEgress : IEgressInterceptor
    {
        public int Order => 10;

        public ValueTask<EgressResult> OnEgressAsync(EgressContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(EgressResult.Modify("x", new Dictionary<string, string> { ["X-Marker"] = "1" }));
    }

    private static ExtensibilityPipeline Create(
        IEnumerable<IIngressInterceptor> ingress,
        IEnumerable<IEgressInterceptor> egress,
        ExtensibilityPipelineOptions? options = null)
        => new(ingress, egress, NullLogger<ExtensibilityPipeline>.Instance,
            Options.Create(options ?? new ExtensibilityPipelineOptions()));

    [Fact]
    public async Task Interceptor_Throws_FailClosed_DeniesRequest()
    {
        var pipeline = Create([new ThrowingIngress()], []);

        var result = await pipeline.ProcessIngressAsync(new IngressContext());

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task Interceptor_Throws_FailOpen_Continues()
    {
        var pipeline = Create([new ThrowingIngress()], [],
            new ExtensibilityPipelineOptions { IngressFailurePolicy = InterceptorFailurePolicy.FailOpen });

        var result = await pipeline.ProcessIngressAsync(new IngressContext());

        result.Decision.ShouldBe(IngressDecision.Continue);
    }

    [Fact]
    public async Task Interceptor_Hangs_TimesOut()
    {
        var pipeline = Create([new HangingIngress()], [],
            new ExtensibilityPipelineOptions { InterceptorTimeoutMs = 100 });

        var result = await pipeline.ProcessIngressAsync(new IngressContext());

        result.Decision.ShouldBe(IngressDecision.Deny);
    }

    [Fact]
    public async Task EgressInterceptor_Throws_DefaultFailOpen_ContinuesWithOtherInterceptors()
    {
        var pipeline = Create([], [new ThrowingEgress(), new MarkerEgress()]);

        var result = await pipeline.ProcessEgressAsync(new EgressContext { IngressContext = new IngressContext() });

        result.AdditionalHeaders.ShouldContainKey("X-Marker");
    }

    [Fact]
    public async Task EgressInterceptor_Throws_FailClosed_Propagates()
    {
        var pipeline = Create([], [new ThrowingEgress()],
            new ExtensibilityPipelineOptions { EgressFailurePolicy = InterceptorFailurePolicy.FailClosed });

        await Should.ThrowAsync<InvalidOperationException>(async () => await pipeline.ProcessEgressAsync(new EgressContext { IngressContext = new IngressContext() }));
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowedAsInterceptorFailure()
    {
        var pipeline = Create([new HangingIngress()], []);
        using var cts = new CancellationTokenSource(50);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await pipeline.ProcessIngressAsync(new IngressContext(), cts.Token));
    }
}
