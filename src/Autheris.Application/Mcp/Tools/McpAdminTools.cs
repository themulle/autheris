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

    private const string ConfirmationHint =
        "Two-phase (ADR-03): the first call only returns a planId; confirm it with TOTP (POST /api/governance/plans/{planId}/confirm), " +
        "then repeat the call with the same arguments plus planId and confirmationToken.";

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
            Description: "Registers a new datasource in inactive state (SEC M-30). Credentials stored safely in vault. " + ConfirmationHint,
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
                "dryRun": { "type": "boolean" },
                "planId": { "type": "string", "description": "Plan identifier returned by the first (planning) call." },
                "confirmationToken": { "type": "string", "description": "HMAC-signed confirmation token of the TOTP-confirmed plan." }
              }
            }
            """,
            TargetGraphQLOperation: string.Empty,
            TargetTable: AdminControlPlaneTable);

        yield return new McpToolDefinition(
            Name: SetDatasetState,
            Description: "Changes dataset lifecycle state (active, quarantined, deprecated, inactive) with WORM audit (R-58). " + ConfirmationHint,
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["datasetId", "state"],
              "properties": {
                "datasetId": { "type": "string" },
                "state": { "type": "string", "enum": ["active", "quarantined", "deprecated", "inactive"] },
                "reason": { "type": "string" },
                "planId": { "type": "string", "description": "Plan identifier returned by the first (planning) call." },
                "confirmationToken": { "type": "string", "description": "HMAC-signed confirmation token of the TOTP-confirmed plan." }
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
            Description: "Sets the tags and/or the catalog visibility tier (Hidden, Summary, Full) of a dataset (issue #111). Full is an administrator act and every change is audited. " + ConfirmationHint,
            InputJsonSchema: """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset identifier (domain.schema.table or table id)." },
                "tags": { "type": "array", "items": { "type": "string" }, "description": "Replaces the admin tags (empty list removes them). Omit to leave unchanged." },
                "visibility": { "type": "string", "enum": ["Hidden", "Summary", "Full"], "description": "Catalog tier. Omit to leave unchanged." },
                "planId": { "type": "string", "description": "Plan identifier returned by the first (planning) call." },
                "confirmationToken": { "type": "string", "description": "HMAC-signed confirmation token of the TOTP-confirmed plan." }
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
                var result = await planningService.PlanAccessAsync(req, adminSid, sessionContext.TenantId, ct: ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, ApplyAccess, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminApplyAccessRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_apply_access.");
                var result = await planningService.ApplyAccessAsync(req, adminSid, sessionContext.TenantId, ct: ct).ConfigureAwait(false);
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
                if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Domain))
                {
                    throw new ArgumentException("admin_register_datasource requires 'name' and 'domain'.");
                }

                // Two-phase confirmation (ADR-03): even a dry run stores an inline secret, so every registration is confirmed.
                var pending = await RequireConfirmationAsync(
                    RegisterDatasource, $"{req.Domain}.{req.Name}", JsonSerializer.Serialize(req, CanonicalJsonOptions), argumentsJson, adminSid, sessionContext, planningService, ct).ConfigureAwait(false);
                if (pending != null)
                {
                    return pending;
                }

                var result = await planningService.RegisterDatasourceAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, SetDatasetState, StringComparison.OrdinalIgnoreCase))
            {
                var req = JsonSerializer.Deserialize<AdminSetDatasetStateRequest>(argumentsJson, JsonOptions)
                    ?? throw new ArgumentException("Invalid arguments for admin_set_dataset_state.");
                if (string.IsNullOrWhiteSpace(req.DatasetId) || string.IsNullOrWhiteSpace(req.State))
                {
                    throw new ArgumentException("admin_set_dataset_state requires 'datasetId' and 'state'.");
                }

                var pending = await RequireConfirmationAsync(
                    SetDatasetState, req.DatasetId, JsonSerializer.Serialize(req, CanonicalJsonOptions), argumentsJson, adminSid, sessionContext, planningService, ct).ConfigureAwait(false);
                if (pending != null)
                {
                    return pending;
                }

                var result = await planningService.SetDatasetStateAsync(req, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
                return new McpToolCallResult(true, JsonSerializer.Serialize(result, JsonOptions));
            }

            if (string.Equals(toolName, SetDatasetCatalog, StringComparison.OrdinalIgnoreCase))
            {
                return await SetDatasetCatalogAsync(argumentsJson, sessionContext, planningService, datasetCatalog, ct).ConfigureAwait(false);
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
            // The control plane raises SecurityException only with its own fixed texts (plan, confirmation and tenant checks).
            return new McpToolCallResult(false, "{}", sex.Message);
        }
        catch (RowFilterEvaluationValidationException rex)
        {
            // Curated, fixed-text reason (names caps and rule types, never stored data).
            return new McpToolCallResult(false, "{}", rex.Reason);
        }
        catch (Autheris.Application.Governance.Grants.GrantPolicyViolationException gex)
        {
            return new McpToolCallResult(false, "{}", $"Grant policy violation ({gex.ErrorCode}).");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or FormatException)
        {
            // Fixed text: a parser or validation message can echo the arguments, inline secrets included.
            return new McpToolCallResult(false, "{}", $"Invalid arguments for admin tool '{toolName}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fixed text: store, vault and repository errors can carry connection strings, secret references or internal names.
            return new McpToolCallResult(false, "{}", $"Admin tool '{toolName}' failed.");
        }
    }

    /// <summary>Property names of the second, confirming call of a mutating admin tool; they are not part of the confirmed arguments.</summary>
    private sealed record ConfirmationArguments(string? PlanId, string? ConfirmationToken);

    // Canonical form of the typed arguments for the confirmation digest (declaration order, no indentation, case-sensitive names).
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Two-phase confirmation of a mutating admin tool (ADR-03, the mechanism of <c>admin_apply_access</c>). Without <c>planId</c> and
    /// <c>confirmationToken</c> the call only plans the operation: the plan binds the tool and the SHA-256 digest of the canonical
    /// arguments and is answered with its planId (nothing is changed). The administrator confirms the plan with TOTP
    /// (<c>POST /api/governance/plans/{planId}/confirm</c>) and repeats the call with the same arguments plus <c>planId</c> and
    /// <c>confirmationToken</c>; the token is verified and consumed once before the operation runs (null is returned then). Any
    /// mismatch throws <see cref="SecurityException"/>.
    /// </summary>
    private static async Task<McpToolCallResult?> RequireConfirmationAsync(
        string operation,
        string target,
        string canonicalArguments,
        string argumentsJson,
        string adminSid,
        McpSessionContext sessionContext,
        IAccessPlanningService planningService,
        CancellationToken ct)
    {
        var confirmation = JsonSerializer.Deserialize<ConfirmationArguments>(argumentsJson, JsonOptions);
        var digest = ArgumentsDigest(operation, canonicalArguments);
        var hasPlan = !string.IsNullOrWhiteSpace(confirmation?.PlanId);
        var hasToken = !string.IsNullOrWhiteSpace(confirmation?.ConfirmationToken);

        if (!hasPlan && !hasToken)
        {
            var plan = await planningService.PlanAdminOperationAsync(operation, target, digest, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
            if (plan is null)
            {
                // Fail closed: without a stored plan there is nothing the administrator could confirm.
                return new McpToolCallResult(false, "{}", $"Admin tool '{operation}' could not create a confirmation plan.");
            }

            return new McpToolCallResult(true, JsonSerializer.Serialize(new
            {
                status = "CONFIRMATION_REQUIRED",
                operation,
                target,
                planId = plan.PlanId,
                expiresAt = plan.ExpiresAt,
                nextStep = $"Confirm the plan with TOTP (POST /api/governance/plans/{plan.PlanId}/confirm), then call '{operation}' again with the same arguments plus 'planId' and 'confirmationToken'."
            }, JsonOptions));
        }

        if (!hasPlan || !hasToken)
        {
            throw new SecurityException("Both 'planId' and 'confirmationToken' are required to apply a confirmed administrator operation.");
        }

        await planningService.ConsumeAdminOperationAsync(
            new AdminApplyAccessRequest(confirmation!.PlanId!, confirmation.ConfirmationToken!), operation, digest, adminSid, sessionContext.TenantId, ct).ConfigureAwait(false);
        return null;
    }

    private static string ArgumentsDigest(string operation, string canonicalArguments) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operation + "\n" + canonicalArguments)));

    private sealed record SetDatasetCatalogArguments(string? Dataset, IReadOnlyList<string>? Tags, string? Visibility, string? PlanId = null, string? ConfirmationToken = null);

    /// <summary>
    /// Issue #111 (A-8): the same service as REST and the manifest, called with the principal rebuilt from the session, so the tool cannot widen
    /// anything beyond what the session principal may do (Full needs an administrator class, decided by the service, not by this tool).
    /// </summary>
    private static async Task<McpToolCallResult> SetDatasetCatalogAsync(
        string argumentsJson,
        McpSessionContext sessionContext,
        IAccessPlanningService planningService,
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

        // Two-phase confirmation (ADR-03): the confirmed arguments are the edit itself, without the confirmation properties.
        var pending = await RequireConfirmationAsync(
            SetDatasetCatalog,
            req.Dataset,
            JsonSerializer.Serialize(req with { PlanId = null, ConfirmationToken = null }, CanonicalJsonOptions),
            argumentsJson,
            sessionContext.UserSid ?? sessionContext.ServicePrincipalId,
            sessionContext,
            planningService,
            ct).ConfigureAwait(false);
        if (pending != null)
        {
            return pending;
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
