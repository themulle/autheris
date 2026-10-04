namespace Autheris.Tests.Unit.Security;

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Common;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class TraceContextAndAuditSecurityTests
{
    [Fact]
    public void TraceContextResolver_WhenActivityPresent_ReturnsNormalized32HexTraceId()
    {
        // Arrange
        using var activitySource = new ActivitySource("Autheris.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("SecurityAuditOperation");
        activity.ShouldNotBeNull();

        // Act
        var traceId = TraceContextResolver.GetCurrentTraceId();

        // Assert
        traceId.ShouldNotBeNullOrWhiteSpace();
        traceId.Length.ShouldBe(32);
        traceId.ShouldBe(activity.TraceId.ToHexString());
    }

    [Fact]
    public void TraceContextResolver_WhenNoActivityPresent_GeneratesValidNonEmptyFallback()
    {
        // Arrange
        Activity.Current = null;

        // Act
        var traceId = TraceContextResolver.GetCurrentTraceId();

        // Assert
        traceId.ShouldNotBeNullOrWhiteSpace();
        traceId.Length.ShouldBeGreaterThanOrEqualTo(32);
    }

    [Fact]
    public async Task AuditLineageEgressInterceptor_IncludesTraceIdInHeadersAndLineageHash()
    {
        // Arrange
        using var activitySource = new ActivitySource("Autheris.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("EgressAuditOp");
        var expectedTraceId = activity!.TraceId.ToHexString();

        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Extensibility = new ExtensibilityOptions { Enabled = true }
        });

        var interceptor = new AuditLineageEgressInterceptor(options);
        var ingressContext = new IngressContext
        {
            Method = "POST",
            Path = "/graphql",
            Headers = new Dictionary<string, string>(),
            Items = new Dictionary<string, object?>()
        };

        var responseBody = "{\"data\":{\"customers\":[{\"id\":\"C1\"}]}}";
        var egressContext = new EgressContext
        {
            IngressContext = ingressContext,
            StatusCode = 200,
            ResponseBodyText = responseBody
        };

        // Act
        var result = await interceptor.OnEgressAsync(egressContext);

        // Assert
        result.AdditionalHeaders.ShouldContainKey("X-Trace-Id");
        result.AdditionalHeaders["X-Trace-Id"].ShouldBe(expectedTraceId);

        result.AdditionalHeaders.ShouldContainKey("X-Audit-Lineage-Hash");
        var lineageHash = result.AdditionalHeaders["X-Audit-Lineage-Hash"];
        lineageHash.Length.ShouldBe(64);

        // Verify cryptographic hash binds traceId with payload
        var expectedBytes = Encoding.UTF8.GetBytes($"{expectedTraceId}:{responseBody}");
        var expectedHashHex = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
        lineageHash.ShouldBe(expectedHashHex);
    }
}
