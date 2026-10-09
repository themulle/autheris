namespace Autheris.Application.Mcp.Tools;

using System;
using System.Collections.Generic;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Model Context Protocol (MCP) administrative tool definitions for access planning and governance.
/// </summary>
public static class McpAdminTools
{
    public const string PlanAccess = "admin_plan_access";
    public const string ApplyAccess = "admin_apply_access";
    public const string RegisterDatasource = "admin_register_datasource";
    public const string SetDatasetState = "admin_set_dataset_state";
    public const string ResolvePrincipal = "admin_resolve_principal";

    private static readonly TableIdentifier AdminControlPlaneTable = new("governance", "admin", "control_plane");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static bool IsAdminTool(string? toolName) =>
        string.Equals(toolName, PlanAccess, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ApplyAccess, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, RegisterDatasource, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SetDatasetState, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ResolvePrincipal, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<McpToolDefinition> Definitions()
    {
        yield return new McpToolDefinition(
            Name: PlanAccess,
            Description: "Generates a two-phase access plan preview with column diffs and PII warnings without mutating state (R-60).",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["datasetId", "grants"],
              "properties": {
                "datasetId": { "type": "string", "description": "Target dataset identifier." },
                "grants": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["principal", "columns"],
                    "properties": {
                      "principal": { "type": "string" },
                      "columns": { "type": "object" },
                      "rowFilter": { "type": "string" },
                      "validUntil": { "type": "string" }
                    }
                  }
                },
                "reason": { "type": "string", "description": "Business justification." }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: ApplyAccess,
            Description: "Applies an approved access plan. Requires a valid HMAC confirmation token issued via TOTP (R-62).",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["planId", "confirmationToken"],
              "properties": {
                "planId": { "type": "string", "description": "Plan identifier." },
                "confirmationToken": { "type": "string", "description": "HMAC-signed confirmation token." }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: RegisterDatasource,
            Description: "Registers a new datasource in inactive state (SEC M-30). Credentials stored safely in vault.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["name", "domain"],
              "properties": {
                "name": { "type": "string" },
                "domain": { "type": "string" },
                "specContent": { "type": "string" },
                "specUrl": { "type": "string" },
                "baseUrl": { "type": "string" },
                "auth": { "type": "object" },
                "dryRun": { "type": "boolean" }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: SetDatasetState,
            Description: "Changes dataset lifecycle state (active, quarantined, deprecated, inactive) with WORM audit (R-58).",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["datasetId", "state"],
              "properties": {
                "datasetId": { "type": "string" },
                "state": { "type": "string", "enum": ["active", "quarantined", "deprecated", "inactive"] },
                "reason": { "type": "string" }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: ResolvePrincipal,
            Description: "Fuzzy / unsharp resolution of principal names into SIDs and groups (R-61).",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "Name, account, or SID to resolve." }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);
    }

    public static async ValueTask<McpToolCallResult> ExecuteAdminToolAsync(
        string toolName,
        string argumentsJson,
        McpSessionContext sessionContext,
        IAccessPlanningService planningService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(planningService);

        var adminSid = sessionContext.UserSid ?? sessionContext.ServicePrincipalId;

        try
        {
            if (string.Equals(toolName, PlanAccess, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminPlanAccessRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_plan_access.");
                var result = await planningService.PlanAccessAsync(req, adminSid, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, ApplyAccess, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminApplyAccessRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_apply_access.");
                var result = await planningService.ApplyAccessAsync(req, adminSid, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, RegisterDatasource, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminRegisterDatasourceRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_register_datasource.");
                var result = await planningService.RegisterDatasourceAsync(req, adminSid, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, SetDatasetState, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminSetDatasetStateRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_set_dataset_state.");
                var result = await planningService.SetDatasetStateAsync(req, adminSid, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, ResolvePrincipal, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminResolvePrincipalRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_resolve_principal.");
                var result = await planningService.ResolvePrincipalAsync(req, adminSid, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            return new McpToolCallResult(false, "{}", $"Unsupported admin tool '{toolName}'.");
        }
        catch (SecurityException sex)
        {
            return new McpToolCallResult(false, "{}", sex.Message);
        }
        catch (Exception ex)
        {
            return new McpToolCallResult(false, "{}", ex.Message);
        }
    }
}
