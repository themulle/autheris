namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>
/// Virtual filters (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md, phase 2): administration API.
/// FilterAdmin maintains filters and access profiles, FilterSync applies the state of the file repository (Talos),
/// GovernanceAdmin and SecurityAuditor read. Callers stay within their tenant; only ClusterAdmin acts across tenants.
/// </summary>
public static class VirtualFilterEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapVirtualFilterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1/governance").RequireAuthorization();

        group.MapGet("/virtual-filters", (HttpContext context, VirtualFilterAdministrationService service) => ListAsync(context, service));
        group.MapPut("/virtual-filters/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => PutFilterAsync(name, context, service));
        group.MapPost("/virtual-filters/{name}/approve", (string name, HttpContext context, VirtualFilterAdministrationService service) => ApproveFilterAsync(name, context, service));
        group.MapDelete("/virtual-filters/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => DeleteFilterAsync(name, context, service));
        group.MapPut("/access-profiles/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => PutProfileAsync(name, context, service));
        group.MapPost("/access-profiles/{name}/approve", (string name, HttpContext context, VirtualFilterAdministrationService service) => ApproveProfileAsync(name, context, service));
        group.MapDelete("/access-profiles/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => DeleteProfileAsync(name, context, service));
        group.MapPost("/virtual-filters/sync/plan", (HttpContext context, VirtualFilterAdministrationService service) => PlanSyncAsync(context, service));
        group.MapPost("/virtual-filters/sync/apply", (HttpContext context, VirtualFilterAdministrationService service) => ApplySyncAsync(context, service));
        group.MapGet("/config-sync/status", (HttpContext context, VirtualFilterAdministrationService service, IOptions<GatewayOptions>? options) =>
            ConfigSyncStatusAsync(context, service, options));
        group.MapGet("/virtual-filters/sync/status", (HttpContext context, VirtualFilterAdministrationService service, IOptions<GatewayOptions>? options) =>
            ConfigSyncStatusAsync(context, service, options));
        group.MapGet("/effective-filters", (HttpContext context, MandatoryRowFilterResolver resolver, Autheris.Application.Interfaces.ITableMetadataRepository catalog) =>
            EffectiveFiltersAsync(context, resolver, catalog));
        return app;
    }

    // ------------------------------------------------------------------ handlers

    internal static async Task<IResult> ListAsync(HttpContext context, VirtualFilterAdministrationService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanRead(security))
        {
            return Forbidden();
        }

        var snapshot = await service.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);
        bool all = security.IsClusterAdmin;
        return Results.Ok(new
        {
            generation = snapshot.Generation,
            filters = snapshot.Filters.Where(f => all || f.TenantId == security.TenantId).Select(FilterView),
            profiles = snapshot.Profiles.Where(p => all || p.TenantId == security.TenantId).Select(ProfileView)
        });
    }

    internal static Task<IResult> PutFilterAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            var body = await ReadAsync<FilterBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            var saved = await service.SaveFilterAsync(body.ToModel(name, tenant.Value), actor, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(FilterView(saved));
        });

    internal static Task<IResult> ApproveFilterAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.GovernanceAdmin, async (security, actor) =>
        {
            var tenant = ResolveTenant(security, context.Request.Query["tenant"]);
            if (tenant == null)
            {
                return Forbidden();
            }

            var expectedHash = context.Request.Query["hash"].FirstOrDefault() ?? context.Request.Query["expected_hash"].FirstOrDefault();
            var approved = await service.ApproveFilterAsync(tenant.Value, name, actor, expectedHash, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(FilterView(approved));
        });

    internal static Task<IResult> DeleteFilterAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            await service.DeleteFilterAsync(security.TenantId, name, actor, context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

    internal static Task<IResult> PutProfileAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            var body = await ReadAsync<ProfileBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            var saved = await service.SaveProfileAsync(body.ToModel(name, tenant.Value), actor, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ProfileView(saved));
        });

    internal static Task<IResult> ApproveProfileAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.GovernanceAdmin, async (security, actor) =>
        {
            var tenant = ResolveTenant(security, context.Request.Query["tenant"]);
            if (tenant == null)
            {
                return Forbidden();
            }

            var expectedHash = context.Request.Query["hash"].FirstOrDefault() ?? context.Request.Query["expected_hash"].FirstOrDefault();
            var approved = await service.ApproveProfileAsync(tenant.Value, name, actor, expectedHash, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ProfileView(approved));
        });

    internal static Task<IResult> DeleteProfileAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            await service.DeleteProfileAsync(security.TenantId, name, actor, context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

    internal static async Task<IResult> PlanSyncAsync(HttpContext context, VirtualFilterAdministrationService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(GatewayRole.FilterSync) && !security.HasRole(GatewayRole.FilterAdmin))
        {
            return Forbidden();
        }

        return await MapErrorsAsync(async () =>
        {
            var request = await ReadSyncRequestAsync(context, security).ConfigureAwait(false);
            return request == null ? Forbidden() : Results.Ok(await service.PlanSyncAsync(request, context.RequestAborted).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the file repository state. FilterSync applies; a sync that removes many bindings (see
    /// <c>VirtualFilters:MaxRemovals</c>) only goes through with <c>?force=true</c>, which needs FilterAdmin.
    /// </summary>
    internal static async Task<IResult> ApplySyncAsync(HttpContext context, VirtualFilterAdministrationService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        bool force = string.Equals(context.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);
        // SR15-07: Sync requires FilterSync role; force=true requires BOTH FilterSync AND FilterAdmin roles
        if (!security.HasRole(GatewayRole.FilterSync) || (force && !security.HasRole(GatewayRole.FilterAdmin)))
        {
            return Forbidden();
        }

        return await MapErrorsAsync(async () =>
        {
            var request = await ReadSyncRequestAsync(context, security).ConfigureAwait(false);
            if (request == null)
            {
                return Forbidden();
            }

            var actor = new VirtualFilterActor(security.UserSid, IsSync: true);
            return Results.Ok(await service.ApplySyncAsync(request, actor, force, context.RequestAborted).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 6: for <c>user</c> (SID, optional <c>groups</c> and <c>roles</c> as comma separated lists) and <c>table</c>
    /// (domain.schema.table) which bindings apply, which columns are missing, what supersedes what, the decision and the
    /// SQL in the table's dialect. Without <c>table</c>: one line per catalog object (<c>page</c>, <c>pageSize</c>).
    /// </summary>
    internal static async Task<IResult> EffectiveFiltersAsync(
        HttpContext context,
        MandatoryRowFilterResolver resolver,
        Autheris.Application.Interfaces.ITableMetadataRepository catalog)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(GatewayRole.FilterAdmin) && !security.HasRole(GatewayRole.GovernanceAdmin) && !security.HasRole(GatewayRole.SecurityAuditor))
        {
            return Forbidden();
        }

        var query = context.Request.Query;
        string? user = query["user"];
        if (string.IsNullOrWhiteSpace(user))
        {
            return Results.Json(new { error = "The query parameter 'user' (SID) is required." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var tenant = ResolveTenant(security, query["tenant"]);
        if (tenant == null)
        {
            return Forbidden();
        }

        static HashSet<T> List<T>(string? value, Func<string, T> create) =>
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(create).ToHashSet();
        var groups = List<Sid>(query["groups"], v => new Sid(v));
        var roles = List<string>(query["roles"], v => v);
        MandatoryFilterQuery For(TableMetadata table) => new(new Sid(user), groups, roles, tenant.Value, table);

        string? tableName = query["table"];
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            if (!TableIdentifier.TryParse(tableName, out var id))
            {
                return Results.Json(new { error = "The query parameter 'table' must be domain.schema.table." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var table = await catalog.GetTableMetadataAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (table == null)
            {
                return Results.Json(new { error = $"The table '{tableName}' is not in the catalog." }, statusCode: StatusCodes.Status404NotFound);
            }

            var explanation = await resolver.ExplainAsync(For(table), context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ExplanationView(user, table, explanation));
        }

        int page = int.TryParse(query["page"], out var p) && p > 0 ? p : 1;
        int pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 500) : 100;
        var tables = (await catalog.GetAllTablesAsync(context.RequestAborted).ConfigureAwait(false))
            .OrderBy(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var objects = new List<object>();
        foreach (var table in tables.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var explanation = await resolver.ExplainAsync(For(table), context.RequestAborted).ConfigureAwait(false);
            objects.Add(new
            {
                table = table.Identifier.ToString(),
                decision = explanation.Decision,
                applied_filters = explanation.Outcome.AppliedFilters,
                reason = explanation.Outcome.DenyReason
            });
        }

        return Results.Ok(new { user, page, page_size = pageSize, total = tables.Count, objects });
    }

    private static object ExplanationView(string user, TableMetadata table, MandatoryFilterExplanation explanation) => new
    {
        user,
        table = table.Identifier.ToString(),
        dialect = table.Dialect.ToString(),
        generation = explanation.Generation,
        decision = explanation.Decision,
        reason = explanation.Outcome.DenyReason,
        sql = explanation.Outcome.PredicateSql,
        applied_filters = explanation.Outcome.AppliedFilters,
        profiles = explanation.Profiles.Select(p => new
        {
            profile = p.Profile,
            scope = p.Scope,
            in_scope = p.InScope,
            uncovered = p.Uncovered?.ToString().ToLowerInvariant(),
            bindings = p.Bindings.Select(b => new
            {
                filter = b.Filter,
                pattern = b.Pattern,
                applies = b.Applies,
                reason = b.Reason,
                missing_columns = b.MissingColumns,
                superseded_by = b.SupersededBy
            })
        })
    };

    // ------------------------------------------------------------------ helpers

    internal static async Task<IResult> ConfigSyncStatusAsync(
        HttpContext context,
        VirtualFilterAdministrationService service,
        IOptions<GatewayOptions>? options = null)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanRead(security))
        {
            return Forbidden();
        }

        var snapshot = await service.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);
        var opt = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();

        var filters = snapshot.Filters.Where(f => security.IsClusterAdmin || f.TenantId == security.TenantId).ToList();
        var profiles = snapshot.Profiles.Where(p => security.IsClusterAdmin || p.TenantId == security.TenantId).ToList();

        var managedFilters = filters.Where(f => f.ManagedBy != null).ToList();
        var managedProfiles = profiles.Where(p => p.ManagedBy != null).ToList();

        var latestCommit = managedFilters.Select(f => f.ManagedBy!.Commit)
            .Concat(managedProfiles.Select(p => p.ManagedBy!.Commit))
            .LastOrDefault() ?? "none";

        var latestSyncAt = managedFilters.Select(f => (DateTimeOffset?)f.UpdatedAt)
            .Concat(managedProfiles.Select(p => (DateTimeOffset?)p.UpdatedAt))
            .DefaultIfEmpty(null)
            .Max();

        return Results.Ok(new
        {
            generation = snapshot.Generation,
            status = "InSync",
            lastSyncAt = latestSyncAt,
            lastCommit = latestCommit,
            filterCount = filters.Count,
            profileCount = profiles.Count,
            managedFilterCount = managedFilters.Count,
            managedProfileCount = managedProfiles.Count,
            maxRemovals = opt.MaxRemovals,
            requireApproval = opt.RequireApproval
        });
    }

    private static bool CanRead(SecurityPrincipalContext security) =>
        security.HasRole(GatewayRole.FilterAdmin) || security.HasRole(GatewayRole.FilterSync) ||
        security.HasRole(GatewayRole.GovernanceAdmin) || security.HasRole(GatewayRole.SecurityAuditor);

    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        GatewayRole requiredRole,
        Func<SecurityPrincipalContext, VirtualFilterActor, Task<IResult>> action)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(requiredRole))
        {
            return Forbidden();
        }

        var actor = new VirtualFilterActor(security.UserSid, IsSync: false);
        return await MapErrorsAsync(() => action(security, actor)).ConfigureAwait(false);
    }

    private static async Task<IResult> MapErrorsAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ManagedResourceLockedException or VirtualFilterConflictException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>The caller's tenant, or the requested one for ClusterAdmin (decision 3); null when forbidden.</summary>
    private static TenantId? ResolveTenant(SecurityPrincipalContext security, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, security.TenantId.Value, StringComparison.Ordinal))
        {
            return security.TenantId;
        }

        return security.IsClusterAdmin ? new TenantId(requested) : (TenantId?)null;
    }

    private static async Task<T> ReadAsync<T>(HttpContext context)
    {
        return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false)
               ?? throw new ArgumentException("The request body is empty.");
    }

    private static async Task<VirtualFilterSyncRequest?> ReadSyncRequestAsync(HttpContext context, SecurityPrincipalContext security)
    {
        var body = await ReadAsync<SyncBody>(context).ConfigureAwait(false);
        var tenant = ResolveTenant(security, body.Tenant);
        if (tenant == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.Commit))
        {
            throw new ArgumentException("A sync needs the path and commit of the file repository.");
        }

        return new VirtualFilterSyncRequest(
            tenant.Value,
            new ManagedBy(body.Path, body.Commit),
            (body.Filters ?? []).Select(p => p.Value.ToModel(p.Key, tenant.Value)).ToList(),
            (body.Profiles ?? []).Select(p => p.Value.ToModel(p.Key, tenant.Value)).ToList());
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private static object FilterView(VirtualFilter f) => new
    {
        id = f.Id,
        tenant = f.TenantId.Value,
        name = f.Name,
        source = f.Source,
        sql = f.Sql,
        target_columns = f.TargetKeyColumns,
        key_columns = f.KeyColumns,
        from = f.Structured == null ? null : new { table = $"{f.Structured.From.Schema}.{f.Structured.From.TableName}", alias = f.Structured.FromAlias },
        joins = f.Structured?.Joins.Select(j => new { table = $"{j.Table.Schema}.{j.Table.TableName}", alias = j.Alias, left = j.LeftColumn, right = j.RightColumn }),
        where = f.Structured?.Where.Select(w => new { column = w.Column, op = ConditionOperatorName(w.Operator), value = w.Value }),
        valid_from = f.ValidFromColumn,
        valid_to = f.ValidToColumn,
        supersedes = f.Supersedes,
        managed_by = f.ManagedBy,
        definition_hash = f.ComputeDefinitionHash(),
        status = f.Status.ToString(),
        pending_deletion = f.PendingDeletion,
        has_draft = f.Draft != null,
        draft_hash = f.Draft?.ComputeDefinitionHash(),
        created_by = f.CreatedBy?.Value,
        approved_by = f.ApprovedBy?.Value,
        approved_at = f.ApprovedAt,
        updated_by = f.UpdatedBy,
        updated_at = f.UpdatedAt
    };

    private static object ProfileView(AccessProfile p) => new
    {
        id = p.Id,
        tenant = p.TenantId.Value,
        name = p.Name,
        grantee = new { type = p.GranteeType.ToString().ToLowerInvariant(), sid = p.GranteeSid?.Value, role = p.RoleName },
        scope = p.Scope,
        uncovered = p.Uncovered?.ToString().ToLowerInvariant(),
        bindings = p.Bindings.Select(b => new
        {
            filter = b.FilterName,
            target = b.TargetPattern,
            object_kinds = b.ObjectKinds.ToString(),
            time_column = b.TimeColumn,
            map = b.ColumnMap
        }),
        managed_by = p.ManagedBy,
        definition_hash = p.ComputeDefinitionHash(),
        status = p.Status.ToString(),
        pending_deletion = p.PendingDeletion,
        has_draft = p.Draft != null,
        draft_hash = p.Draft?.ComputeDefinitionHash(),
        created_by = p.CreatedBy?.Value,
        approved_by = p.ApprovedBy?.Value,
        approved_at = p.ApprovedAt,
        updated_by = p.UpdatedBy,
        updated_at = p.UpdatedAt
    };

    private static string ConditionOperatorName(FilterConditionOperator op) => op switch
    {
        FilterConditionOperator.Eq => "eq",
        FilterConditionOperator.NotEq => "neq",
        FilterConditionOperator.IsNull => "is_null",
        _ => "is_not_null"
    };

    // ------------------------------------------------------------------ request bodies (shape of the Talos files)

    internal sealed class TableRef
    {
        [JsonPropertyName("table")] public string Table { get; set; } = string.Empty;
        [JsonPropertyName("alias")] public string Alias { get; set; } = string.Empty;
    }

    internal sealed class JoinBody
    {
        [JsonPropertyName("table")] public string Table { get; set; } = string.Empty;
        [JsonPropertyName("alias")] public string Alias { get; set; } = string.Empty;
        [JsonPropertyName("left")] public string Left { get; set; } = string.Empty;
        [JsonPropertyName("right")] public string Right { get; set; } = string.Empty;
    }

    internal sealed class ConditionBody
    {
        [JsonPropertyName("column")] public string Column { get; set; } = string.Empty;
        [JsonPropertyName("op")] public string Op { get; set; } = string.Empty;
        [JsonPropertyName("value")] public string? Value { get; set; }
    }

    internal sealed class FilterBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("key_columns")] public List<string>? KeyColumns { get; set; }
        [JsonPropertyName("from")] public TableRef? From { get; set; }
        [JsonPropertyName("joins")] public List<JoinBody>? Joins { get; set; }
        [JsonPropertyName("where")] public List<ConditionBody>? Where { get; set; }
        [JsonPropertyName("valid_from")] public string? ValidFrom { get; set; }
        [JsonPropertyName("valid_to")] public string? ValidTo { get; set; }
        [JsonPropertyName("supersedes")] public List<string>? Supersedes { get; set; }
        [JsonPropertyName("sql")] public string? Sql { get; set; }

        public VirtualFilter ToModel(string name, TenantId tenant)
        {
            if (Sql != null)
            {
                return new VirtualFilter
                {
                    TenantId = tenant,
                    Name = name,
                    Source = Source,
                    Sql = Sql,
                    KeyColumns = KeyColumns ?? [],
                    ValidFromColumn = ValidFrom,
                    ValidToColumn = ValidTo,
                    Structured = From == null ? null : new StructuredFilterDefinition(),
                    Supersedes = Supersedes ?? []
                };
            }

            if (From == null)
            {
                throw new ArgumentException("A virtual filter needs 'from' or 'sql'.");
            }

            return new VirtualFilter
            {
                TenantId = tenant,
                Name = name,
                Source = Source,
                KeyColumns = KeyColumns ?? [],
                Structured = new StructuredFilterDefinition
                {
                    From = Table(Source, From.Table),
                    FromAlias = From.Alias,
                    Joins = (Joins ?? []).Select(j => new FilterJoin(Table(Source, j.Table), j.Alias, j.Left, j.Right)).ToList(),
                    Where = (Where ?? []).Select(w => new FilterCondition(w.Column, ParseOperator(w.Op), w.Value)).ToList()
                },
                ValidFromColumn = ValidFrom,
                ValidToColumn = ValidTo,
                Supersedes = Supersedes ?? []
            };
        }

        private static TableIdentifier Table(string source, string schemaAndTable)
        {
            var parts = schemaAndTable.Split('.');
            if (parts.Length != 2)
            {
                throw new ArgumentException($"'{schemaAndTable}' must be schema.table (the data source comes from 'source').");
            }

            return new TableIdentifier(source, parts[0], parts[1]);
        }

        private static FilterConditionOperator ParseOperator(string op) => op.ToLowerInvariant() switch
        {
            "eq" => FilterConditionOperator.Eq,
            "neq" => FilterConditionOperator.NotEq,
            "is_null" => FilterConditionOperator.IsNull,
            "is_not_null" => FilterConditionOperator.IsNotNull,
            _ => throw new ArgumentException($"Unknown condition operator '{op}' (eq, neq, is_null, is_not_null).")
        };
    }

    internal sealed class GranteeBody
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "user";
        [JsonPropertyName("sid")] public string? Sid { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
    }

    internal sealed class BindingBody
    {
        [JsonPropertyName("filter")] public string Filter { get; set; } = string.Empty;
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("object_kinds")] public List<string>? ObjectKinds { get; set; }
        [JsonPropertyName("time_column")] public string? TimeColumn { get; set; }
        [JsonPropertyName("map")] public Dictionary<string, string>? Map { get; set; }

        public FilterBinding ToModel()
        {
            var kinds = FilterObjectKinds.None;
            foreach (var kind in ObjectKinds ?? ["relation", "procedure_result"])
            {
                kinds |= kind.ToLowerInvariant() switch
                {
                    "relation" => FilterObjectKinds.Relation,
                    "procedure_result" => FilterObjectKinds.ProcedureResult,
                    _ => throw new ArgumentException($"Unknown object kind '{kind}' (relation, procedure_result).")
                };
            }

            return new FilterBinding
            {
                FilterName = Filter,
                TargetPattern = Target,
                ObjectKinds = kinds,
                TimeColumn = TimeColumn,
                ColumnMap = Map
            };
        }
    }

    internal sealed class ProfileBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("grantee")] public GranteeBody? Grantee { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
        [JsonPropertyName("uncovered")] public string? Uncovered { get; set; }
        [JsonPropertyName("bindings")] public List<BindingBody>? Bindings { get; set; }

        public AccessProfile ToModel(string name, TenantId tenant)
        {
            var grantee = Grantee ?? throw new ArgumentException("A profile needs a grantee.");
            var type = grantee.Type.ToLowerInvariant() switch
            {
                "user" => GranteeType.User,
                "group" => GranteeType.Group,
                "role" => GranteeType.Role,
                "service_principal" => GranteeType.ServicePrincipal,
                _ => throw new ArgumentException($"Unknown grantee type '{grantee.Type}'.")
            };

            return new AccessProfile
            {
                TenantId = tenant,
                Name = name,
                GranteeType = type,
                GranteeSid = string.IsNullOrWhiteSpace(grantee.Sid) ? (Sid?)null : new Sid(grantee.Sid),
                RoleName = grantee.Role,
                Scope = Scope,
                Uncovered = Uncovered?.ToLowerInvariant() switch
                {
                    null => null,
                    "deny" => UncoveredPolicy.Deny,
                    "skip" => UncoveredPolicy.Skip,
                    _ => throw new ArgumentException($"Unknown uncovered '{Uncovered}' (deny, skip).")
                },
                Bindings = (Bindings ?? []).Select(b => b.ToModel()).ToList()
            };
        }
    }

    internal sealed class SyncBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("commit")] public string Commit { get; set; } = string.Empty;
        [JsonPropertyName("filters")] public Dictionary<string, FilterBody>? Filters { get; set; }
        [JsonPropertyName("profiles")] public Dictionary<string, ProfileBody>? Profiles { get; set; }
    }
}
