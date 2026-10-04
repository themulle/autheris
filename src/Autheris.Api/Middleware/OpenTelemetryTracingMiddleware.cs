namespace Autheris.Api.Middleware;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Trace;

public sealed class OpenTelemetryTracingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("Ingress.HttpRequest", ActivityKind.Server);
        if (activity != null)
        {
            var tenantId = context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var tObj) && tObj is TenantId t
                ? t
                : TenantId.LegacySingleTenant;

            var userSid = context.User.GetUserSid()?.Value ?? "anonymous";

            GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "tenant.id", tenantId.Value);
            GatewayDiagnostics.SetSafeTag(activity, "Governance.EvaluateConsent", "user.sid", userSid);
        }

        try
        {
            await next(context);
            if (activity != null)
            {
                if (context.Response.StatusCode >= 400)
                {
                    activity.SetStatus(ActivityStatusCode.Error, $"HTTP {context.Response.StatusCode}");
                }
                else
                {
                    activity.SetStatus(ActivityStatusCode.Ok);
                }
            }
        }
        catch (Exception ex)
        {
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                activity.AddException(ex);
            }
            throw;
        }
    }
}
