namespace Autheris.Tests.Unit;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Middleware;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public class IngressEgressTests
{
    [Fact]
    public async Task AuditLineageEgressInterceptor_ComputesSha256LineageHashAndGovernanceHeaders()
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new AuditLineageEgressInterceptor(options);

        var responsePayload = "{\"data\":{\"user\":{\"name\":\"Alice\"}}}";
        var responseBytes = Encoding.UTF8.GetBytes(responsePayload);

        var ingressContext = new IngressContext();

        var egressContext = new EgressContext
        {
            IngressContext = ingressContext,
            StatusCode = 200,
            ResponseBytes = responseBytes,
            ResponseBodyText = responsePayload
        };

        var result = await interceptor.OnEgressAsync(egressContext);

        result.AdditionalHeaders.ShouldContainKey("X-Trace-Id");
        var actualTraceId = result.AdditionalHeaders["X-Trace-Id"];
        actualTraceId.Length.ShouldBe(32);

        var prefix = Encoding.UTF8.GetBytes($"{actualTraceId}:");
        var boundBytes = new byte[prefix.Length + responseBytes.Length];
        Buffer.BlockCopy(prefix, 0, boundBytes, 0, prefix.Length);
        Buffer.BlockCopy(responseBytes, 0, boundBytes, prefix.Length, responseBytes.Length);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(boundBytes));

        result.AdditionalHeaders.ShouldContainKey("X-Audit-Lineage-Hash");
        result.AdditionalHeaders["X-Audit-Lineage-Hash"].ShouldBe(expectedHash);
        result.AdditionalHeaders["X-Governance-Status"].ShouldBe("Evaluated");
        result.AdditionalHeaders["X-Governance-Mode"].ShouldBe("Standard-Zero-Trust");
    }

    [Fact]
    public async Task ExtensibilityPipeline_ExecutesOrderedInterceptorsAndStopsOnDenial()
    {
        var interceptor1 = new OrderedTestInterceptor(10, IngressResult.Continue());
        var interceptor2 = new OrderedTestInterceptor(20, IngressResult.Deny("Blocked by order 20"));
        var interceptor3 = new OrderedTestInterceptor(30, IngressResult.Continue());

        var pipeline = new ExtensibilityPipeline(
            [interceptor1, interceptor2, interceptor3],
            [],
            NullLogger<ExtensibilityPipeline>.Instance);

        var context = new IngressContext();
        var result = await pipeline.ProcessIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.Reason.ShouldBe("Blocked by order 20");
        interceptor1.Executed.ShouldBeTrue();
        interceptor2.Executed.ShouldBeTrue();
        interceptor3.Executed.ShouldBeFalse(); // Stopped early!
    }

    private sealed class OrderedTestInterceptor(int order, IngressResult result) : IIngressInterceptor
    {
        public int Order => order;
        public bool Executed { get; private set; }

        public ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
        {
            Executed = true;
            return ValueTask.FromResult(result);
        }
    }
}
