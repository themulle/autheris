namespace Autheris.Api.Middleware;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Applies HTTP security response headers (CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy),
/// OData v4 protocol version headers, and Development insecure-mode diagnostic headers (WP-ARC-C-04).
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private const string StrictCsp = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
    private const string NitroToolCsp = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; worker-src 'self' blob:; connect-src 'self'; manifest-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";

    private readonly RequestDelegate _next;
    private readonly GatewayOptions _options;
    private readonly string _nitroToolPath;
    private readonly bool _allowNitroToolCsp;
    private readonly string? _insecureModeHeader;

    public SecurityHeadersMiddleware(RequestDelegate next, GatewayOptions options, IHostEnvironment env)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(env);

        _nitroToolPath = options.GraphQL.BananaCakePopPath.StartsWith('/')
            ? options.GraphQL.BananaCakePopPath
            : "/" + options.GraphQL.BananaCakePopPath;
        _allowNitroToolCsp = env.IsDevelopment() && options.GraphQL.EnableBananaCakePop;

        var devSecurity = DevFeatures.Resolve(options, env.IsDevelopment()).DevSecurity;
        if (env.IsDevelopment() && (options.HasAnySecurityBypassActive || devSecurity.Count > 0))
        {
            _insecureModeHeader = string.Join("; ",
                options.GetAllActiveBypasses().Concat(devSecurity.Select(s => "DEV-SECURITY:" + s)));
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");

        var isToolPath = _allowNitroToolCsp &&
                         context.Request.Path.StartsWithSegments(_nitroToolPath, StringComparison.OrdinalIgnoreCase);
        context.Response.Headers.Append("Content-Security-Policy", isToolPath ? NitroToolCsp : StrictCsp);

        if (_options.OData.Enabled && context.Request.Path.StartsWithSegments("/odata", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["OData-Version"] = "4.0";
        }

        if (_insecureModeHeader != null)
        {
            context.Response.Headers.Append("X-Gateway-Insecure-Mode", _insecureModeHeader);
        }

        return _next(context);
    }
}
