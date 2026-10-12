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
using Autheris.Domain.Common;
using Autheris.Domain.Model;
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

        await middleware.InvokeAsync(context, _auditRepo, auditContext);

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
            middleware.InvokeAsync(context, _auditRepo, auditContext));

        thrown.Message.ShouldBe("Database crashed");
        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("ERROR");
        recorded.DetailsJson.ShouldContain("Database crashed");
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
            middleware.InvokeAsync(context, _auditRepo, auditContext));

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

        await middleware.InvokeAsync(context, _auditRepo, auditContext);

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

        await middleware.InvokeAsync(context, _auditRepo, auditContext);

        recorded.ShouldNotBeNull();
        recorded.Decision.ShouldBe("DENY");
        recorded.EventType.ShouldBe(expectedEventType);
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
        await middleware.InvokeAsync(context, _auditRepo, auditContext);

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
}
