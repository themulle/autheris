namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class FinOpsEndpoints
{
    public static IEndpointRouteBuilder MapFinOpsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/v1/finops/focus - FOCUS Cost & Usage Records
        app.MapGet("/api/v1/finops/focus", async (
            HttpRequest request,
            IFinOpsAccountingService accountingService) =>
        {
            var user = request.HttpContext.User;
            var isAuthorized = user.IsInRole("BillingAdmin") || GatewayPolicies.HasAnyRole(user, [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin]);
            if (!isAuthorized)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var fromStr = request.Query["from"].ToString();
            var toStr = request.Query["to"].ToString();
            var tenantId = request.Query["tenantId"].ToString();
            var format = request.Query["format"].ToString();

            var from = DateTimeOffset.TryParse(fromStr, out var f) ? f : DateTimeOffset.UtcNow.AddDays(-30);
            var to = DateTimeOffset.TryParse(toStr, out var t) ? t : DateTimeOffset.UtcNow.AddDays(1);

            // SEC M-4 / SG-30: Non-canonical cluster admins can only query their own tenant
            var isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(user);
            if (!isClusterAdmin)
            {
                tenantId = EndpointSecurity.GetRequestTenant(request.HttpContext).Value;
            }

            var records = new List<FocusCostRecord>();
            await foreach (var rec in accountingService.GetRecordsAsync(from, to, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId, request.HttpContext.RequestAborted))
            {
                records.Add(rec);
            }

            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildFocusCsv(records);
                return Results.Text(csv, "text/csv");
            }

            return Results.Ok(new
            {
                specVersion = "1.2",
                chargePeriodStart = from.ToString("O"),
                chargePeriodEnd = to.ToString("O"),
                count = records.Count,
                records
            });
        });

        // GET /api/v1/finops/budget/{tenantId}
        app.MapGet("/api/v1/finops/budget/{tenantId}", async (
            string tenantId,
            HttpRequest request,
            IFinOpsAccountingService accountingService) =>
        {
            var user = request.HttpContext.User;
            var isAuthorized = user.IsInRole("BillingAdmin") || GatewayPolicies.HasAnyRole(user, [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin]);
            if (!isAuthorized)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var callerTenant = EndpointSecurity.GetRequestTenant(request.HttpContext).Value;

            // Only canonical cluster admins may read other tenants' budgets (IDOR prevention);
            // GovernanceAdmin is tenant-scoped here.
            var isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(user);
            if (!isClusterAdmin && (string.IsNullOrWhiteSpace(callerTenant) || !string.Equals(callerTenant, tenantId, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var status = await accountingService.CheckBudgetAsync(tenantId, request.HttpContext.RequestAborted);
            return Results.Ok(status);
        });

        return app;
    }

    private static string BuildFocusCsv(IReadOnlyList<FocusCostRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ChargePeriodStart,ChargePeriodEnd,BilledCost,EffectiveCost,Currency,ConsumedQuantity,ConsumedUnit,SubAccountId,ResourceId,ServiceName,PricingCategory");

        foreach (var r in records)
        {
            sb.Append(EscapeCsvField(r.ChargePeriodStart)).Append(',');
            sb.Append(EscapeCsvField(r.ChargePeriodEnd)).Append(',');
            sb.Append(r.BilledCost).Append(',');
            sb.Append(r.EffectiveCost).Append(',');
            sb.Append(EscapeCsvField(r.Currency)).Append(',');
            sb.Append(r.ConsumedQuantity).Append(',');
            sb.Append(EscapeCsvField(r.ConsumedUnit)).Append(',');
            sb.Append(EscapeCsvField(r.SubAccountId)).Append(',');
            sb.Append(EscapeCsvField(r.ResourceId)).Append(',');
            sb.Append(EscapeCsvField(r.ServiceName)).Append(',');
            sb.AppendLine(EscapeCsvField(r.PricingCategory));
        }

        return sb.ToString();
    }

    private static string EscapeCsvField(string? val)
    {
        if (string.IsNullOrEmpty(val))
        {
            return "\"\"";
        }

        // Neutralize CSV formula injection (CWE-1236)
        if (val.StartsWith('=') || val.StartsWith('+') || val.StartsWith('-') || val.StartsWith('@') || val.StartsWith('\t') || val.StartsWith('\r'))
        {
            val = "'" + val;
        }

        return "\"" + val.Replace("\"", "\"\"") + "\"";
    }
}
