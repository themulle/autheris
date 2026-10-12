namespace Autheris.Api.Endpoints;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class DataContractEndpoints
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapDataContractEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. Declarative Data Contract Apply API
        app.MapPost("/api/v1/governance/contracts/apply", async (
            HttpContext context,
            IDataContractEngine contractEngine) =>
        {
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                10 * 1024 * 1024,
                "Contract payload exceeds maximum allowed size (10 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            try
            {
                var contract = JsonSerializer.Deserialize<DataContract>(payload, CaseInsensitiveOptions);

                if (contract == null)
                {
                    return Results.BadRequest(new { error = "Invalid contract payload." });
                }

                await contractEngine.RegisterContractAsync(contract, context.RequestAborted);
                return Results.Ok(new
                {
                    success = true,
                    contractId = contract.Id,
                    table = contract.Table.ToString(),
                    assertionsCount = contract.Assertions.Count,
                    isHealthy = contract.IsHealthy
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization()
          .WithRequestBodyLimit(10 * 1024 * 1024)
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // 2. Query Data Contracts
        app.MapGet("/api/v1/governance/contracts", async (
            HttpContext context,
            IDataContractEngine contractEngine) =>
        {
            var contracts = await contractEngine.ListContractsAsync(context.RequestAborted);
            return Results.Ok(contracts);
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // 3. DataHub Inbound Webhook / Events Emitter
        app.MapPost("/api/v1/catalog/datahub/events", async (
            HttpContext context,
            IDataHubCatalogClient dataHubClient) =>
        {
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                10 * 1024 * 1024,
                "Event payload exceeds maximum allowed size (10 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            var success = await dataHubClient.IngestMetadataChangeEventAsync(payload, context.RequestAborted);
            return success ? Results.Ok(new { received = true }) : Results.BadRequest(new { error = "Invalid event payload." });
        }).RequireAuthorization()
          .WithRequestBodyLimit(10 * 1024 * 1024)
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }
}
