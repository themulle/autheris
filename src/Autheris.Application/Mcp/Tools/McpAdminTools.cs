namespace Autheris.Application.Mcp.Tools;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

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
    public const string PlanRowFilterEvaluation = "admin_plan_row_filter_evaluation";
    public const string ApplyRowFilterEvaluation = "admin_apply_row_filter_evaluation";
    public const string SetDatasetCatalog = "admin_set_dataset_catalog";

    private static readonly TableIdentifier AdminControlPlaneTable = new("governance", "admin", "control_plane");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    // Issue #107 (SEC-RF107-14b): the row filter evaluation tools reject unknown properties; the strategy is a string, so numbers fail to bind.
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static bool IsAdminTool(string? toolName) =>
        string.Equals(toolName, PlanRowFilterEvaluation, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ApplyRowFilterEvaluation, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, PlanAccess, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ApplyAccess, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, RegisterDatasource, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SetDatasetState, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SetDatasetCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ResolvePrincipal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SR-H-02 / SR-M-09: The admin tools mutate the shared catalog and ReBAC state, which is not tenant scoped, so only the
    /// canonical cluster administrator (<see cref="ClusterAdminPolicy.IsClusterAdminRole"/>) may call or even see them.
    /// Tenant-scoped admin roles (TenantAdmin, GovernanceAdmin, Admin, ...) never qualify, and read-only sessions never
    /// qualify (not even under the MCP auth bypass).
    /// </summary>
    public static bool CanUseAdminTools(McpSessionContext? session, bool authBypassed)
    {
        if (session == null || session.IsReadOnly)
        {
            return false;
        }

        return authBypassed || (session.Roles ?? []).Any(ClusterAdminPolicy.IsClusterAdminRole);
    }

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
            Name: PlanRowFilterEvaluation,
            Description: "Plans a change of the key-set cache settings of a correlated row filter rule (rowFilterId) or of a dataset (datasetId): " +
                         "strategy, duration and maximum keys, validated against the global caps, without mutating state. Confirm with TOTP, then apply with admin_apply_row_filter_evaluation (issue #107).",
            InputJsonSchema: """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "rowFilterId": { "type": "string", "description": "Rule level: id of an ALLOW correlated row filter of the caller's tenant (exclusive with datasetId)." },
                "datasetId": { "type": "string", "description": "Object level: table id or domain.schema.table (exclusive with rowFilterId). All values omitted clears the object level." },
                "strategy": { "type": ["string", "null"], "enum": ["Exists", "KeySetCache", null], "description": "Evaluation strategy; omitted or null = inherit." },
                "durationSeconds": { "type": ["integer", "null"], "minimum": 1, "description": "Cache duration in seconds, at most the configured maximum; omitted = inherit." },
                "maxKeys": { "type": ["integer", "null"], "minimum": 1, "description": "Maximum number of keys, at most the configured maximum; omitted = inherit." },
                "reason": { "type": "string", "description": "Business justification." }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: ApplyRowFilterEvaluation,
            Description: "Applies a confirmed row filter evaluation plan. Requires a valid HMAC confirmation token issued via TOTP; rejects access plans and plans whose stored settings changed since planning (issue #107).",
            InputJsonSchema: """
            {
              "type": "object",
              "additionalProperties": false,
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
            Name: SetDatasetCatalog,
            Description: "Sets the tags and/or the catalog visibility tier (Hidden, Summary, Full) of a dataset (issue #111). Single call; Full is an administrator act and every change is audited.",
            InputJsonSchema: """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset identifier (domain.schema.table or table id)." },
                "tags": { "type": "array", "items": { "type": "string" }, "description": "Replaces the admin tags (empty list removes them). Omit to leave unchanged." },
                "visibility": { "type": "string", "enum": ["Hidden", "Summary", "Full"], "description": "Catalog tier. Omit to leave unchanged." }
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
        Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService? datasetCatalog = null,
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
                var result = await planningService.PlanAccessAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, ApplyAccess, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminApplyAccessRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_apply_access.");
                var result = await planningService.ApplyAccessAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, PlanRowFilterEvaluation, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminPlanRowFilterEvaluationRequest>(argumentsJson, StrictJsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_plan_row_filter_evaluation.");
                var result = await planningService.PlanRowFilterEvaluationAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, ApplyRowFilterEvaluation, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminApplyAccessRequest>(argumentsJson, StrictJsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_apply_row_filter_evaluation.");
                var result = await planningService.ApplyRowFilterEvaluationAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, RegisterDatasource, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminRegisterDatasourceRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_register_datasource.");
                var result = await planningService.RegisterDatasourceAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, SetDatasetState, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminSetDatasetStateRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_set_dataset_state.");
                var result = await planningService.SetDatasetStateAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, SetDatasetCatalog, StringComparison.OrdinalIgnoreCase))
            {
                return await SetDatasetCatalogAsync(argumentsJson, sessionContext, datasetCatalog, ct).ConfigureAwait(false);
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

    private sealed record SetDatasetCatalogArguments(string? Dataset, IReadOnlyList<string>? Tags, string? Visibility);

    /// <summary>
    /// Issue #111 (A-8): the same service as REST and the manifest, called with the principal rebuilt from the session, so the tool cannot widen
    /// anything beyond what the session principal may do (Full needs an administrator class, decided by the service, not by this tool).
    /// </summary>
    private static async Task<McpToolCallResult> SetDatasetCatalogAsync(
        string argumentsJson,
        McpSessionContext sessionContext,
        Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService? datasetCatalog,
        CancellationToken ct)
    {
        if (datasetCatalog is null)
        {
            return new McpToolCallResult(false, "{}", "UNAVAILABLE");
        }

        var req = JsonSerializer.Deserialize<SetDatasetCatalogArguments>(argumentsJson, StrictJsonOptions)
            ?? throw new ArgumentException("Invalid arguments for admin_set_dataset_catalog.");
        if (string.IsNullOrWhiteSpace(req.Dataset) || (req.Tags is null && req.Visibility is null))
        {
            throw new ArgumentException("admin_set_dataset_catalog requires 'dataset' and at least one of 'tags' or 'visibility'.");
        }

        var principal = Autheris.Application.Mcp.Services.McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
        var edit = new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(req.Tags, null, null, req.Visibility);
        var result = await datasetCatalog.ApplyAsync(principal, req.Dataset, edit, "mcp", ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var code = result.Outcome switch
            {
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Forbidden => "FORBIDDEN",
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidTag => "INVALID_TAG",
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.TagLimitExceeded => "TAG_LIMIT_EXCEEDED",
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidVisibility => "INVALID_VISIBILITY",
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidRequest => "INVALID_REQUEST",
                Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Unavailable => "UNAVAILABLE",
                _ => "NOT_FOUND"
            };
            return new McpToolCallResult(false, "{}", code);
        }

        var settings = result.Settings!;
        return new McpToolCallResult(true, JsonSerializer.Serialize(new
        {
            datasetId = result.Table!.Value.ToString(),
            changed = result.Outcome == Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Applied,
            tags = settings.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).Select(t => new { tag = t.Tag, source = t.Source == DatasetTagSource.Dbt ? "dbt" : "admin" }),
            visibility = settings.Visibility.ToString(),
            tierEffective = result.TierEffective
        }, JsonOptions));
    }
}
