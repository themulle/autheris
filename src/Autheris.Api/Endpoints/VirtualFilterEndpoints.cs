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
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Virtual filters (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md, phase 2): administration API.
/// FilterAdmin maintains filters and bindings, FilterSync applies the state of the file repository (Talos),
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
        group.MapDelete("/virtual-filters/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => DeleteFilterAsync(name, context, service));
        group.MapPut("/filter-bindings/{id:guid}", (Guid id, HttpContext context, VirtualFilterAdministrationService service) => PutBindingAsync(id, context, service));
        group.MapDelete("/filter-bindings/{id:guid}", (Guid id, HttpContext context, VirtualFilterAdministrationService service) => DeleteBindingAsync(id, context, service));
        group.MapPost("/virtual-filters/sync/plan", (HttpContext context, VirtualFilterAdministrationService service) => PlanSyncAsync(context, service));
        group.MapPost("/virtual-filters/sync/apply", (HttpContext context, VirtualFilterAdministrationService service) => ApplySyncAsync(context, service));
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
            bindings = snapshot.Bindings.Where(b => all || b.TenantId == security.TenantId).Select(BindingView)
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

    internal static Task<IResult> DeleteFilterAsync(string name, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            await service.DeleteFilterAsync(security.TenantId, name, actor, context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

    internal static Task<IResult> PutBindingAsync(Guid id, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            var body = await ReadAsync<BindingBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            var saved = await service.SaveBindingAsync(body.ToModel(id, tenant.Value), actor, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(BindingView(saved));
        });

    internal static Task<IResult> DeleteBindingAsync(Guid id, HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterAdmin, async (security, actor) =>
        {
            await service.DeleteBindingAsync(security.TenantId, id, actor, context.RequestAborted).ConfigureAwait(false);
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

    internal static Task<IResult> ApplySyncAsync(HttpContext context, VirtualFilterAdministrationService service) =>
        ExecuteAsync(context, GatewayRole.FilterSync, async (security, actor) =>
        {
            var request = await ReadSyncRequestAsync(context, security).ConfigureAwait(false);
            if (request == null)
            {
                return Forbidden();
            }

            return Results.Ok(await service.ApplySyncAsync(request, actor with { IsSync = true }, context.RequestAborted).ConfigureAwait(false));
        });

    // ------------------------------------------------------------------ helpers

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
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
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
            (body.Bindings ?? []).Select(b => b.ToModel(Guid.NewGuid(), tenant.Value)).ToList());
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private static object FilterView(VirtualFilter f) => new
    {
        id = f.Id,
        tenant = f.TenantId.Value,
        name = f.Name,
        source = f.Source,
        key_columns = f.KeyColumns,
        from = new { table = $"{f.Structured!.From.Schema}.{f.Structured.From.TableName}", alias = f.Structured.FromAlias },
        joins = f.Structured.Joins.Select(j => new { table = $"{j.Table.Schema}.{j.Table.TableName}", alias = j.Alias, left = j.LeftColumn, right = j.RightColumn }),
        where = f.Structured.Where.Select(w => new { column = w.Column, op = ConditionOperatorName(w.Operator), value = w.Value }),
        valid_from = f.ValidFromColumn,
        valid_to = f.ValidToColumn,
        supersedes = f.Supersedes,
        managed_by = f.ManagedBy,
        definition_hash = f.ComputeDefinitionHash(),
        updated_by = f.UpdatedBy,
        updated_at = f.UpdatedAt
    };

    private static object BindingView(FilterBinding b) => new
    {
        id = b.Id,
        tenant = b.TenantId.Value,
        filter = b.FilterName,
        target = b.TargetPattern,
        grantee = new { type = b.GranteeType.ToString().ToLowerInvariant(), sid = b.GranteeSid?.Value, role = b.RoleName },
        object_kinds = b.ObjectKinds.ToString(),
        time_column = b.TimeColumn,
        map = b.ColumnMap,
        on_unmatched = b.OnUnmatched?.ToString().ToLowerInvariant(),
        managed_by = b.ManagedBy,
        definition_hash = b.ComputeDefinitionHash()
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

        public VirtualFilter ToModel(string name, TenantId tenant)
        {
            if (From == null)
            {
                throw new ArgumentException("A virtual filter needs 'from'.");
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
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("filter")] public string Filter { get; set; } = string.Empty;
        [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;
        [JsonPropertyName("grantee")] public GranteeBody? Grantee { get; set; }
        [JsonPropertyName("object_kinds")] public List<string>? ObjectKinds { get; set; }
        [JsonPropertyName("time_column")] public string? TimeColumn { get; set; }
        [JsonPropertyName("map")] public Dictionary<string, string>? Map { get; set; }
        [JsonPropertyName("on_unmatched")] public string? OnUnmatched { get; set; }

        public FilterBinding ToModel(Guid id, TenantId tenant)
        {
            var grantee = Grantee ?? throw new ArgumentException("A binding needs a grantee.");
            var type = grantee.Type.ToLowerInvariant() switch
            {
                "user" => GranteeType.User,
                "group" => GranteeType.Group,
                "role" => GranteeType.Role,
                "service_principal" => GranteeType.ServicePrincipal,
                _ => throw new ArgumentException($"Unknown grantee type '{grantee.Type}'.")
            };

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
                Id = id,
                TenantId = tenant,
                FilterName = Filter,
                TargetPattern = Target,
                GranteeType = type,
                GranteeSid = string.IsNullOrWhiteSpace(grantee.Sid) ? (Sid?)null : new Sid(grantee.Sid),
                RoleName = grantee.Role,
                ObjectKinds = kinds,
                TimeColumn = TimeColumn,
                ColumnMap = Map,
                OnUnmatched = OnUnmatched?.ToLowerInvariant() switch
                {
                    null => null,
                    "deny" => Domain.Model.OnUnmatched.Deny,
                    "skip" => Domain.Model.OnUnmatched.Skip,
                    _ => throw new ArgumentException($"Unknown on_unmatched '{OnUnmatched}' (deny, skip).")
                }
            };
        }
    }

    internal sealed class SyncBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("commit")] public string Commit { get; set; } = string.Empty;
        [JsonPropertyName("filters")] public Dictionary<string, FilterBody>? Filters { get; set; }
        [JsonPropertyName("bindings")] public List<BindingBody>? Bindings { get; set; }
    }
}
