namespace Autheris.Api.Middleware;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Governance.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-GOV-08: Dynamic Schema Contract Routing Middleware.
/// Resolves the applicable contract from headers, claims, or query parameters and enforces
/// isolated schema slice validation for the active request.
/// </summary>
public sealed class SchemaContractMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SchemaContractMiddleware> _logger;

    public const string ContractItemKey = "GatewayContract";

    public SchemaContractMiddleware(
        RequestDelegate next,
        ILogger<SchemaContractMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, ISchemaContractManager? contractManager)
    {
        if (contractManager == null || !contractManager.IsEnabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // API-3: a contract bound to the identity (claim) takes precedence. Header and query string may only select a
        // contract when the identity carries none, or repeat the bound one; they can never widen a partner's view.
        string? claimContract = context.User.FindFirst("contract")?.Value?.Trim();
        string? requestedContract = null;
        if (context.Request.Headers.TryGetValue("X-Gateway-Contract", out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        {
            requestedContract = headerVal.ToString().Trim();
        }
        else if (context.Request.Query.TryGetValue("contract", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
        {
            requestedContract = queryVal.ToString().Trim();
        }

        if (!string.IsNullOrWhiteSpace(claimContract) && !string.IsNullOrWhiteSpace(requestedContract) &&
            !string.Equals(claimContract, requestedContract, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("F-GOV-08 Request rejected: requested contract '{Requested}' differs from the contract bound to the identity.", requestedContract);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string? contractName = !string.IsNullOrWhiteSpace(claimContract) ? claimContract : requestedContract;

        // 4. Default contract
        if (string.IsNullOrWhiteSpace(contractName))
        {
            contractName = contractManager.DefaultContract;
        }

        if (!contractManager.HasContract(contractName))
        {
            _logger.LogWarning("F-GOV-08 Request rejected: Unknown schema contract '{Contract}'", contractName);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";

            var errJson = JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = $"Unknown schema contract '{contractName}'.",
                        extensions = new { code = "INVALID_SCHEMA_CONTRACT", contract = contractName }
                    }
                }
            });

            await context.Response.WriteAsync(errJson, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Items[ContractItemKey] = contractName;
        context.Response.Headers["X-Gateway-Contract"] = contractName;

        await _next(context).ConfigureAwait(false);
    }
}
