namespace Autheris.Api.Middleware;

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-AI-08: FinOps Budget Governance Middleware.
/// Enforces soft and hard budget caps to protect against "Denial of Wallet" and runaway agent loops.
/// </summary>
public sealed class FinOpsBudgetMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FinOpsBudgetMiddleware> _logger;

    public FinOpsBudgetMiddleware(
        RequestDelegate next,
        ILogger<FinOpsBudgetMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IFinOpsAccountingService? accountingService)
    {
        if (accountingService == null || !accountingService.IsEnabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        // Skip FinOps endpoint itself and health probes
        if (path.StartsWith("/api/v1/finops", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // API-4: Anonyme / unauthentifizierte Anfragen belasten kein FinOps-Budget
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string tenantId;
        string principalId;

        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) && item is SecurityPrincipalContext secContext)
        {
            if (!secContext.IsAuthenticated)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            tenantId = secContext.TenantId.Value;
            principalId = secContext.UserSid.Value;
        }
        else
        {
            var rawTenant = context.User.FindFirst("tenant_id")?.Value ??
                            context.User.FindFirst("tid")?.Value ??
                            context.User.FindFirst("tenant")?.Value;

            if (string.IsNullOrWhiteSpace(rawTenant) || !TenantId.TryParse(rawTenant, out var parsedTenant))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            tenantId = parsedTenant.Value;
            principalId = context.User.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value ??
                          context.User.FindFirst("sub")?.Value ??
                          context.User.Identity?.Name ?? "authenticated";
        }

        var budgetStatus = await accountingService.CheckBudgetAsync(tenantId, context.RequestAborted).ConfigureAwait(false);

        if (budgetStatus.IsExceeded)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            var errJson = JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = $"Monthly FinOps budget exceeded for tenant '{tenantId}'.",
                        extensions = new
                        {
                            code = "FINOPS_BUDGET_EXCEEDED",
                            tenantId,
                            currentSpend = budgetStatus.CurrentSpend,
                            budgetLimit = budgetStatus.BudgetLimit
                        }
                    }
                }
            });

            await context.Response.WriteAsync(errJson, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (budgetStatus.IsWarning)
        {
            context.Response.Headers["X-FinOps-Budget-Warning"] = "true";
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            // Record compute consumption asynchronously (minimum 1ms compute metering)
            var computeMs = Math.Max(1, sw.ElapsedMilliseconds);
            await accountingService.RecordUsageAsync(
                tenantId: tenantId,
                principalId: principalId,
                operationName: path,
                category: "HttpCompute",
                promptTokens: 0,
                completionTokens: 0,
                computeMs: computeMs,
                tags: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }
}
