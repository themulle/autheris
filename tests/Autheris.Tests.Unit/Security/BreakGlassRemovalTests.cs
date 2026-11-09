namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class BreakGlassRemovalTests
{
    [Fact]
    public async Task Legacy_justification_and_breakglass_headers_grant_nothing()
    {
        var options = Options.Create(new GatewayOptions());
        var pipeline = new ExtensibilityPipeline(
            [],
            [new AuditLineageEgressInterceptor(options)],
            NullLogger<ExtensibilityPipeline>.Instance);
        var logger = new CapturingLogger<GatewayExtensibilityMiddleware>();

        IngressContext? capturedIngress = null;
        var passthrough = new InspectingInterceptor(ctx => capturedIngress = ctx);
        pipeline = new ExtensibilityPipeline(
            [passthrough],
            [new AuditLineageEgressInterceptor(options)],
            NullLogger<ExtensibilityPipeline>.Instance);

        var middleware = new GatewayExtensibilityMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await ctx.Response.WriteAsync("{\"error\":\"forbidden\"}");
            },
            pipeline,
            options,
            logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/graphql";
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers["X-Break-Glass"] = "secret-breakglass-token-value";
        httpContext.Request.Headers["X-Access-Justification"] = "INC-99999-sensitive-value";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, "BreakGlassOperator")],
            "TestAuth"));
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        // Request still receives 403 Forbidden from downstream; headers never elevate or set IsBreakGlass.
        httpContext.Response.StatusCode.ShouldBe((int)HttpStatusCode.Forbidden);
        capturedIngress.ShouldNotBeNull();
        capturedIngress!.Items.ContainsKey("IsBreakGlass").ShouldBeFalse();
        capturedIngress.Items.ContainsKey("AccessJustification").ShouldBeFalse();
        httpContext.Response.Headers.ContainsKey("X-Audit-Ticket").ShouldBeFalse();

        // Debug log mentions only header names, never header values (SEC-SIMPL-29).
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain("secret-breakglass-token-value");
            entry.Message.ShouldNotContain("INC-99999-sensitive-value");
        }
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Debug && e.Message.Contains("X-Break-Glass"));
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Debug && e.Message.Contains("X-Access-Justification"));
    }

    [Fact]
    public void Profile_breakglass_assignments_removed_and_grant_nothing()
    {
        Enum.GetNames<AccessProfileAssignmentKind>().ShouldNotContain("BreakGlass");

        var functionalRightNames = typeof(FunctionalRight)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(FunctionalRight))
            .Select(f => f.Name)
            .ToList();
        functionalRightNames.ShouldNotContain("SecurityBreakGlass");

        var logger = new CapturingLogger<AccessProfileStore>();
        var store = new AccessProfileStore(
            AccessProfileDialect.PostgreSql,
            (_, _) => throw new NotSupportedException(),
            logger: logger);

        var parsedKind = store.ReadKind("BreakGlass");
        Enum.IsDefined(parsedKind).ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("unknown kind"));
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain("BreakGlass");
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new AccessProfile
        {
            ProfileId = "p-1",
            TenantId = new TenantId("tenant-a"),
            Name = "Profile 1",
            MaskingMode = MaskingPolicyMode.Default,
            TargetTables = ["sales.public.orders"],
            CreatedBy = "admin",
            CreatedAt = now
        };

        var legacyBreakGlassAssignment = new AccessProfileAssignment
        {
            Subject = "alice",
            SubjectType = AccessProfileSubjectType.User,
            AssignedAt = now,
            ExpiresAt = now.AddHours(12),
            CreatedBy = "req",
            ApprovedBy = "approver",
            ApprovedAt = now,
            Justification = "incident-secret",
            Kind = parsedKind,
            RenewalCount = 0
        };

        // At runtime, an unknown assignment kind ('BreakGlass') fails closed and grants nothing.
        AccessProfileAssignmentRules.IsEffective(profile, legacyBreakGlassAssignment, now).ShouldBeFalse();
    }

    [Fact]
    public void No_break_glass_justification_member()
    {
        typeof(Autheris.Domain.Security.SecurityPrincipalContext)
            .GetProperty("BreakGlassJustification", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .ShouldBeNull();

        typeof(Autheris.Domain.Security.SecurityPrincipalContext)
            .GetField("BreakGlassJustification", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .ShouldBeNull();
    }

    [Fact]
    public void No_break_glass_audit_event()
    {
        typeof(Autheris.Domain.Audit.AuditEventTypes)
            .GetField("BreakGlassActivated", BindingFlags.Public | BindingFlags.Static)
            .ShouldBeNull();
    }

    private sealed class InspectingInterceptor(Action<IngressContext> inspect) : IIngressInterceptor
    {
        public int Order => 1;

        public ValueTask<IngressResult> OnIngressAsync(IngressContext context, System.Threading.CancellationToken cancellationToken = default)
        {
            inspect(context);
            return ValueTask.FromResult(IngressResult.Continue());
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
