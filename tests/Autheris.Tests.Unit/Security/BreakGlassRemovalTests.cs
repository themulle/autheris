namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
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
    public async Task Profile_breakglass_assignments_removed_and_grant_nothing()
    {
        Enum.GetNames<AccessProfileAssignmentKind>().ShouldNotContain("BreakGlass");
        Enum.GetNames<FunctionalRight>().ShouldNotContain("SecurityBreakGlass");

        var dbName = $"bg-removal-{Guid.NewGuid():N}";
        var cs = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        using var repo = new SqliteGovernanceRepository(new FakeEpochValidationService(), cs);
        var tenant = new TenantId("tenant-a");
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertProfileAsync(new AccessProfile
        {
            ProfileId = "p-1",
            TenantId = tenant,
            Name = "Profile 1",
            MaskingMode = MaskingPolicyMode.Masked,
            TargetTables = ["sales.public.orders"],
            CreatedBy = "admin",
            CreatedAt = now
        });

        // Simulate a legacy row in ACCESS_PROFILE_ASSIGNMENTS with kind = 'BreakGlass' prior to migration 0006.
        await using (var cmd = keepAlive.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO ACCESS_PROFILE_ASSIGNMENTS
                    (tenant_id, profile_id, subject_type, subject, assigned_at, expires_at, created_by, approved_by, approved_at, justification, kind, renewal_count)
                VALUES
                    ('tenant-a', 'p-1', 'User', 'alice', @now, @exp, 'req', 'approver', @now, 'incident-secret', 'BreakGlass', 0);
                """;
            cmd.Parameters.AddWithValue("@now", now.ToString("O"));
            cmd.Parameters.AddWithValue("@exp", now.AddHours(12).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        // At runtime, an unknown assignment kind ('BreakGlass') fails closed and grants nothing.
        var activeForAlice = await repo.GetActiveProfilesForSubjectAsync(tenant, ["alice"], [], [], now);
        activeForAlice.ShouldBeEmpty();
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
