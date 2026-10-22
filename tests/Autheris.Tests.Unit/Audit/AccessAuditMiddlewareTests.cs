using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

public sealed class AccessAuditMiddlewareTests
{
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly IOptions<GatewayOptions> _options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

    private AccessAuditMiddleware CreateMiddleware(RequestDelegate next, IAuthFailureAggregator? aggregator = null)
    {
        return new AccessAuditMiddleware(
            next,
            _options,
            NullLogger<AccessAuditMiddleware>.Instance,
            aggregator);
    }

    private static HttpContext ContextWithStatus(int status, out AuditContext auditContext)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/protected";
        auditContext = new AuditContext();
        context.Features.Set(auditContext);
        context.Response.StatusCode = status;
        return context;
    }

    [Fact]
    public async Task AccessAuditMiddleware_DenyWithAuditStoreDown_StillReturnsDenial()
    {
        _auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("store down")));
        var context = ContextWithStatus(403, out var auditContext);
        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 403; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        // The denial never depends on the audit outcome: no exception, status untouched, one attempt made with a non-cancelable token.
        context.Response.StatusCode.ShouldBe(403);
        await _auditRepo.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "DENY"), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task AccessAuditMiddleware_AllowWithAuditStoreDown_Throws()
    {
        _auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("store down")));
        var context = ContextWithStatus(200, out var auditContext);
        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));
    }

    [Fact]
    public async Task AccessAuditMiddleware_AuditWrite_DoesNotUseTheRequestToken()
    {
        using var cts = new CancellationTokenSource();
        var context = ContextWithStatus(200, out var auditContext);
        context.RequestAborted = cts.Token;
        var middleware = CreateMiddleware(c => { cts.Cancel(); return Task.CompletedTask; });

        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await _auditRepo.Received(1).RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulRequest_RecordsAllowAuditEntry()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Request.Method = "POST";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.42");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sid", "S-1-5-21-USER1"),
            new Claim("tid", "tenant-alpha")
        ], "TestAuth"));

        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(innerContext =>
        {
            innerContext.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ALLOW");
        recorded.ActorSid.Value.ShouldBe("S-1-5-21-USER1");
        recorded.TenantId.Value.ShouldBe("tenant-alpha");
        recorded.TraceId.ShouldNotBeNullOrEmpty();
        recorded.DetailsJson.ShouldContain("10.0.0.42");
    }

    [Fact]
    public async Task InvokeAsync_WhenExceptionThrown_RecordsErrorAuditEntryAndRethrows()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("Database crashed"));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        thrown.Message.ShouldBe("Database crashed");
        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
        recorded.DetailsJson.ShouldContain("InvalidOperationException");
        recorded.DetailsJson.ShouldNotContain("Database crashed");
    }

    [Fact]
    public async Task InvokeAsync_ExceptionWithPiiMessage_MessageNeverInDetailsJson()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("duplicate key (alice@corp.com)"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.DetailsJson.ShouldNotContain("duplicate key");
        recorded.DetailsJson.ShouldNotContain("alice@corp.example");
        recorded.DetailsJson.ShouldNotContain("alice@corp.com");
        recorded.DetailsJson.ShouldContain("InvalidOperationException");
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_RecordsMappedStatus500NotDefault200()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.DetailsJson.ShouldContain("\"statusCode\":500");
        recorded.DetailsJson.ShouldNotContain("\"statusCode\":200");
    }

    [Fact]
    public async Task InvokeAsync_TimeoutException_RecordsStatus504()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new TimeoutException("Database timeout"));

        await Should.ThrowAsync<TimeoutException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
        recorded.DetailsJson.ShouldContain("\"statusCode\":504");
        recorded.DetailsJson.ShouldContain("TimeoutException");
    }

    [Fact]
    public async Task InvokeAsync_GatewaySecurityException_RecordsDenyWithMappedStatus()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new Autheris.Domain.Exceptions.GatewayForbiddenException("Access denied", "ACCESS_DENIED"));

        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayForbiddenException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("DENY");
        recorded.DetailsJson.ShouldContain("\"statusCode\":403");
        recorded.DetailsJson.ShouldContain("ACCESS_DENIED");
    }

    private sealed class ExceptionWithMaliciousErrorCode : Exception
    {
        public string ErrorCode => "MALICIOUS_CODE\nWith raw data and spaces";
    }

    [Fact]
    public async Task InvokeAsync_ErrorCodeWithIllegalChars_FallsBackToTypeName()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new ExceptionWithMaliciousErrorCode());

        await Should.ThrowAsync<ExceptionWithMaliciousErrorCode>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.DetailsJson.ShouldNotContain("MALICIOUS_CODE");
        recorded.DetailsJson.ShouldContain("ExceptionWithMaliciousErrorCode");
    }

    [Fact]
    public async Task InvokeAsync_WhenRequestAborted_RecordsErrorWithClientAborted()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new DefaultHttpContext();
        context.RequestAborted = cts.Token;
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
        recorded.DetailsJson.ShouldContain("CLIENT_ABORTED");
    }

    [Fact]
    public async Task InvokeAsync_WhenHandledBySpecializedComponent_DoesNotRecordDuplicate()
    {
        var recordedCount = 0;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(_ => recordedCount++), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(innerContext =>
        {
            // Specialized endpoint handles audit itself
            auditContext.MarkHandled();
            innerContext.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recordedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(401, "AUTH_FAILED")]
    [InlineData(403, "AUTHZ_ENDPOINT_DENIED")]
    [InlineData(429, "RATE_LIMIT_EXCEEDED")]
    public async Task InvokeAsync_DenialStatusCodes_RecordDenialAuditEntry(int statusCode, string expectedEventType)
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/protected";
        var auditContext = new AuditContext();
        context.Features.Set(auditContext);

        var middleware = CreateMiddleware(innerContext =>
        {
            innerContext.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("DENY");
        recorded.EventType.ShouldBe(expectedEventType);
    }

    private static void SetDelegatedEndpoint(DefaultHttpContext context) =>
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuditPolicy(AuditLevel.Delegated, AuditEventTypes.AsyncJobSubmitted)),
            "delegated-test"));

    [Theory]
    [InlineData(401, "DENY", "AUTH_FAILED")]
    [InlineData(403, "DENY", "AUTHZ_ENDPOINT_DENIED")]
    [InlineData(404, "DENY", "AUTHZ_ENDPOINT_DENIED")]
    [InlineData(500, "ERROR", "QUERY_EXECUTION_ERROR")]
    public async Task InvokeAsync_DelegatedRoute_NotHandled_FinalizesFailureStatuses(int status, string decision, string eventType)
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/jobs/query";
        SetDelegatedEndpoint(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = status; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe(decision);
        recorded.EventType.ShouldBe(eventType);
    }

    [Fact]
    public async Task InvokeAsync_DelegatedRoute_Unauthorized_FeedsAuthFailureAggregator()
    {
        var aggregator = Substitute.For<IAuthFailureAggregator>();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/jobs/query";
        SetDelegatedEndpoint(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 401; return Task.CompletedTask; }, aggregator);
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await aggregator.Received(1).RecordFailureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InvokeAsync_DelegatedRoute_ExceptionBeforeDomainAudit_RecordsErrorAndRethrows()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/jobs/query";
        SetDelegatedEndpoint(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
    }

    [Fact]
    public async Task InvokeAsync_DelegatedRoute_SuccessWithoutDomainAudit_RecordsNothing()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/jobs/query";
        SetDelegatedEndpoint(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 202; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await _auditRepo.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_DelegatedRoute_Handled_DoesNotDoubleRecordOnFailureStatus()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/jobs/query";
        SetDelegatedEndpoint(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { auditContext.MarkHandled(); c.Response.StatusCode = 500; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await _auditRepo.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    private const string MalformedTenant = "evil<script>tenant";

    private async Task<AuditLogEntry> RunWithClaimsAsync(int status, Action<DefaultHttpContext>? configure, params Claim[] claims)
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/data";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        configure?.Invoke(context);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = status; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
        return recorded;
    }

    [Fact]
    public async Task InvokeAsync_ValidTenantIdWithMalformedTid_AttributesToValidTenant()
    {
        var entry = await RunWithClaimsAsync(200, null,
            new Claim("sid", "S-1-5-21-USER1"),
            new Claim("tenant_id", "tenant-a"),
            new Claim("tid", MalformedTenant));

        entry.TenantId.Value.ShouldBe("tenant-a");
        entry.DetailsJson.ShouldNotContain("evil");
    }

    [Fact]
    public async Task InvokeAsync_UsesTenantFromResolvedSecurityPrincipalContext()
    {
        var entry = await RunWithClaimsAsync(200,
            c => c.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
            {
                UserSid = new Sid("S-1-5-21-USER1"),
                TenantId = new TenantId("tenant-ctx"),
                GroupSids = new HashSet<Sid>(),
                TenantRoles = new HashSet<string>(),
                ClusterRoles = new HashSet<string>(),
                AuthenticationScheme = "TestAuth"
            },
            new Claim("sid", "S-1-5-21-USER1"),
            new Claim("tid", "tenant-claim"));

        entry.TenantId.Value.ShouldBe("tenant-ctx");
    }

    [Fact]
    public async Task InvokeAsync_MalformedTenantClaim_And403_RecordsDenyWithoutRawValue()
    {
        var entry = await RunWithClaimsAsync(403, null,
            new Claim("sid", "S-1-5-21-USER1"),
            new Claim("tid", MalformedTenant));

        entry.Decision.ShouldBe("DENY");
        entry.EventType.ShouldBe(AuditEventTypes.AuthzEndpointDenied);
        entry.DetailsJson.ShouldContain("\"tenantClaim\":\"INVALID\"");
        entry.DetailsJson.ShouldNotContain("evil");
        entry.DetailsJson.ShouldNotContain("script");
        entry.TenantId.Value.ShouldNotContain("evil");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(400)]
    public async Task InvokeAsync_UnroutedAnonymous4xx_DoesNotRecordTableQueryAllow(int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-admin/" + new string('a', 40);
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = status; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await _auditRepo.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        auditContext.IsHandled.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_RoutedEndpoint404_StillRecorded()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/things/42";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "routed-test"));
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 404; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_LongPath_TargetCappedTo256()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/things/" + new string('x', 1000);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "routed-test"));
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        recorded.ShouldNotBeNull();
        recorded.TargetTable.Length.ShouldBe(256);
    }

    [Fact]
    public async Task InvokeAsync_Unrouted401_UsesAggregatorOnly()
    {
        var aggregator = Substitute.For<IAuthFailureAggregator>();
        var context = new DefaultHttpContext();
        context.Request.Path = "/nowhere";
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(c => { c.Response.StatusCode = 401; return Task.CompletedTask; }, aggregator);
        await middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext);

        await aggregator.Received(1).RecordFailureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _auditRepo.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_UnroutedException_StillRecordsError()
    {
        AuditLogEntry? recorded = null;
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/nowhere";
        context.Response.StatusCode = 404;
        var auditContext = new AuditContext();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(_auditRepo), auditContext));

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
    }
}
