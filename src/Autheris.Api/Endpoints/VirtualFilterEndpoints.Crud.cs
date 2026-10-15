namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public static partial class VirtualFilterEndpoints
{
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

            var model = body.ToModel(name, tenant.Value);
            var saved = await service.SaveProfileAsync(model, actor, body.ExplicitKeyBudgets(model), context.RequestAborted).ConfigureAwait(false);
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

    private static object ProfileView(VirtualFilterAccessProfile p) => new
    {
        id = p.Id,
        tenant = p.TenantId.Value,
        name = p.Name,
        grantee = new { type = p.GranteeType.ToString().ToLowerInvariant(), sid = p.GranteeSid?.Value, role = p.RoleName },
        scope = p.Scope,
        uncovered = p.Uncovered is { } uncovered ? VirtualFilterBindingValidator.FormatEnum(uncovered) : null,
        bindings = p.Bindings.Select(b => new
        {
            filter = b.FilterName,
            target = b.TargetPattern,
            object_kinds = b.ObjectKinds.ToString(),
            time_column = b.TimeColumn,
            map = b.ColumnMap,
            strategy = VirtualFilterBindingValidator.FormatEnum(b.Strategy),
            pushdown_format = b.PushdownFormat is { } format ? VirtualFilterBindingValidator.FormatEnum(format) : null,
            max_pushdown_keys = b.MaxPushdownKeys
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
        [JsonPropertyName("strategy")] public string? Strategy { get; set; }
        [JsonPropertyName("pushdown_format")] public string? PushdownFormat { get; set; }
        [JsonPropertyName("max_pushdown_keys")] public int? MaxPushdownKeys { get; set; }

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

            var binding = new FilterBinding
            {
                FilterName = Filter,
                TargetPattern = Target,
                ObjectKinds = kinds,
                TimeColumn = TimeColumn,
                ColumnMap = Map,
                Strategy = Strategy == null
                    ? VirtualFilterExecutionStrategy.Adaptive
                    : VirtualFilterBindingValidator.ParseEnum<VirtualFilterExecutionStrategy>(Strategy, "strategy"),
                PushdownFormat = PushdownFormat == null
                    ? null
                    : VirtualFilterBindingValidator.ParseEnum<PushdownParameterFormat>(PushdownFormat, "pushdown_format"),
            };

            // An omitted field keeps the model's own default; only an explicit value is a request.
            return MaxPushdownKeys is { } keys ? binding with { MaxPushdownKeys = keys } : binding;
        }
    }

    internal sealed class ProfileBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("grantee")] public GranteeBody? Grantee { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
        [JsonPropertyName("uncovered")] public string? Uncovered { get; set; }
        [JsonPropertyName("bindings")] public List<BindingBody>? Bindings { get; set; }

        /// <summary>The bindings of <paramref name="model"/> (built by <see cref="ToModel"/>) whose body stated <c>max_pushdown_keys</c>.</summary>
        public HashSet<FilterBinding> ExplicitKeyBudgets(VirtualFilterAccessProfile model)
        {
            var set = new HashSet<FilterBinding>(ReferenceEqualityComparer.Instance);
            foreach (var (body, binding) in (Bindings ?? []).Zip(model.Bindings))
            {
                if (body.MaxPushdownKeys.HasValue)
                {
                    set.Add(binding);
                }
            }

            return set;
        }

        public VirtualFilterAccessProfile ToModel(string name, TenantId tenant)
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

            return new VirtualFilterAccessProfile
            {
                TenantId = tenant,
                Name = name,
                GranteeType = type,
                GranteeSid = string.IsNullOrWhiteSpace(grantee.Sid) ? (Sid?)null : new Sid(grantee.Sid),
                RoleName = grantee.Role,
                Scope = Scope,
                Uncovered = Uncovered == null ? null : VirtualFilterBindingValidator.ParseEnum<UncoveredPolicy>(Uncovered, "uncovered", ignoreCase: true),
                Bindings = (Bindings ?? []).Select(b => b.ToModel()).ToList()
            };
        }
    }
}
