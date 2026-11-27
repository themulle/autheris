namespace Autheris.Api.Middleware;

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Applies the MCP output guardrails (prompt-injection sanitizing, PII scrubbing) to GraphQL and WebSQL responses for agent tokens,
/// so the protection depends on the token scope and not on the transport. Fail closed: a response that cannot be inspected
/// (streaming, non-JSON, above the buffer cap) is withheld and never passed through. App tokens are not touched.
/// </summary>
public sealed class AgentOutputGuardMiddleware
{
    internal const int MaxGuardedBytes = 16 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly AgentOutputGuard _guard;
    private readonly GatewayOptions _options;
    private readonly ILogger<AgentOutputGuardMiddleware> _logger;
    private readonly string _graphQlPath;

    public AgentOutputGuardMiddleware(
        RequestDelegate next,
        AgentOutputGuard guard,
        IOptions<GatewayOptions> options,
        ILogger<AgentOutputGuardMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphQlPath = _options.GraphQL.EndpointPath.StartsWith('/', StringComparison.Ordinal)
            ? _options.GraphQL.EndpointPath
            : "/" + _options.GraphQL.EndpointPath;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuardedRoute(context) || !IsAgentCaller(context.User, _options))
        {
            await _next(context);
            return;
        }

        if (GatewayExtensibilityMiddleware.IsStreamingRequest(context))
        {
            await ApiProblems.WriteAsync(
                context, StatusCodes.Status406NotAcceptable, ApiErrorCodes.NotAcceptable,
                "Streaming responses are not available to agent tokens because they cannot be inspected; request a plain JSON response.",
                context.RequestAborted);
            return;
        }

        var original = context.Response.Body;
        var buffer = new CappedBufferStream(MaxGuardedBytes);
        context.Response.Body = buffer;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        if (context.Response.HasStarted)
        {
            // The inner pipeline bypassed the buffer; nothing can be withheld any more.
            _logger.LogError("Agent output guard could not inspect a response that had already started on {Path}.", context.Request.Path);
            return;
        }

        if (buffer.Overflowed)
        {
            ResetResponse(context);
            await ApiProblems.WriteAsync(
                context, StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge,
                "The result is too large to be inspected for an agent token; narrow the query or lower the row limit.",
                context.RequestAborted);
            return;
        }

        if (buffer.Length == 0)
        {
            return;
        }

        if (!IsJson(context.Response.ContentType))
        {
            ResetResponse(context);
            await ApiProblems.WriteAsync(
                context, StatusCodes.Status406NotAcceptable, ApiErrorCodes.NotAcceptable,
                "Only JSON responses are available to agent tokens because other formats cannot be inspected.",
                context.RequestAborted);
            return;
        }

        AgentOutputGuardResult result;
        try
        {
            result = _guard.Apply(context.Request.Path.Value ?? "agent-output", Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent output guard failed; the response is withheld.");
            ResetResponse(context);
            await ApiProblems.WriteAsync(
                context, StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError,
                "The response could not be inspected and was withheld.", context.RequestAborted);
            return;
        }

        if (result.Modified)
        {
            RecordTelemetry(context, result);
        }

        var bytes = Encoding.UTF8.GetBytes(result.Body);
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>True when the token carries a configured agent scope or a scope mapped to the AutonomousAgents tier.</summary>
    internal static bool IsAgentCaller(ClaimsPrincipal? principal, GatewayOptions options)
    {
        if (principal is null)
        {
            return false;
        }

        var agentScopes = options.Mcp.GetEffectiveAgentScopes();
        var scopeTiers = options.ResourceGroups?.ScopeTiers;
        foreach (var claim in principal.Claims)
        {
            if (!string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(claim.Type, "http://schemas.microsoft.com/identity/claims/scope", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var raw in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var configured in agentScopes)
                {
                    if (TokenAccessScope.IsSameScope(configured, raw))
                    {
                        return true;
                    }
                }

                if (scopeTiers is { Count: > 0 })
                {
                    foreach (var (configuredScope, tier) in scopeTiers)
                    {
                        if (tier == ResourceGroupTier.AutonomousAgents && TokenAccessScope.IsSameScope(configuredScope, raw))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private bool IsGuardedRoute(HttpContext context)
    {
        if (GatewayEndpointMetadata.HasTransport(context, DataSourceTransportFlags.GraphQL) ||
            GatewayEndpointMetadata.HasTransport(context, DataSourceTransportFlags.WebSql))
        {
            return true;
        }

        var path = context.Request.Path;
        return path.StartsWithSegments(_graphQlPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/api/v1/sql", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/api/sql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var media = contentType.Split(';', 2)[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetResponse(HttpContext context)
    {
        context.Response.ContentLength = null;
        context.Response.ContentType = null;
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.Headers.Remove("Content-Disposition");
    }

    private static void RecordTelemetry(HttpContext context, AgentOutputGuardResult result)
    {
        var details = context.Features.Get<AuditContext>()?.AdditionalDetails;
        if (details is not null)
        {
            details["agentOutputInjectionWithheld"] = result.InjectionWithheld;
            details["agentOutputPiiScrubbed"] = result.PiiScrubbed;
        }

        Activity.Current?.SetTag("autheris.agent_output_guard.modified", true);
    }

    /// <summary>Write-only buffer that stops collecting above its cap and reports the overflow instead of growing.</summary>
    private sealed class CappedBufferStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly long _cap;

        public CappedBufferStream(long cap) => _cap = cap;

        public bool Overflowed { get; private set; }

        public ReadOnlySpan<byte> WrittenSpan =>
            _buffer.TryGetBuffer(out var seg) ? seg.AsSpan(0, (int)_buffer.Length) : _buffer.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Overflowed)
            {
                return;
            }

            if (_buffer.Length + buffer.Length > _cap)
            {
                Overflowed = true;
                _buffer.SetLength(0);
                return;
            }

            _buffer.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
    }
}
