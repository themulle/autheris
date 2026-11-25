namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Application.Governance.SingleApproval;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Caller attributes (129 A-7, A-11): administration API under<c>/api/v1/governance/caller-attributes</c>. A write
/// (save or delete) takes effect at once and returns the applied state; GovernanceAdmin or CallerAttributeApprover writes (an attribute-table
/// or service-set change needs the approver role, BC-129-03); the simulation needs GovernanceAdmin or FilterAdmin, and revealing values the
/// CallerAttributeInspector role. Every response omits attribute values; callers stay within their tenant, only ClusterAdmin acts across tenants.
/// </summary>
public static class CallerAttributeAdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapCallerAttributeAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1/governance/caller-attributes")
            .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("", (HttpContext context, CallerAttributeAdministrationService service) => ListAsync(context, service))
            .RequireAuthorization(GatewayPolicies.CallerAttributeRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPut("/{name}", (string name, HttpContext context, CallerAttributeAdministrationService service) => PutDefinitionAsync(name, context, service))
            .RequireAuthorization(GatewayPolicies.CallerAttributeApprove);
        group.MapDelete("/{name}", (string name, HttpContext context, CallerAttributeAdministrationService service) => DeleteDefinitionAsync(name, context, service))
            .RequireAuthorization(GatewayPolicies.CallerAttributeApprove);
        group.MapGet("/service-sets", (HttpContext context, CallerAttributeAdministrationService service) => ListServiceSetsAsync(context, service))
            .RequireAuthorization(GatewayPolicies.CallerAttributeRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPut("/service-sets", (HttpContext context, CallerAttributeAdministrationService service) => PutServiceSetAsync(context, service))
            .RequireAuthorization(GatewayPolicies.CallerAttributeApprove);
        group.MapPost("/simulate", (HttpContext context, CallerAttributeSimulationService simulation) => SimulateAsync(context, simulation))
            .RequireAuthorization(GatewayPolicies.CallerAttributeSimulate)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CallerAttributeSimulationRead);
        return app;
    }

    // ---- definitions -----------------------------------------------------------------------------------------------------------------

    internal static async Task<IResult> ListAsync(HttpContext context, CallerAttributeAdministrationService service)
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
            attributes = snapshot.Attributes.Where(a => all || a.Definition.Tenant == security.TenantId).Select(a => new
            {
                tenant = a.Definition.Tenant.Value,
                name = a.Definition.Name,
                status = a.Definition.Status.ToString(),
                hash = a.DefinitionHash,
                value_type = a.Definition.ValueType.ToString(),
                cardinality = a.Definition.Cardinality.ToString(),
                max_values = a.Definition.MaxValues,
                ttl_seconds = a.Definition.TtlSeconds,
                provider = new { kind = a.Definition.Provider.Kind, config = a.Definition.Provider.Config },
                created_by = a.CreatedBy,
                approved_by = a.ApprovedBy
            })
        });
    }

    internal static Task<IResult> PutDefinitionAsync(string name, HttpContext context, CallerAttributeAdministrationService service) =>
        ExecuteAsync(context, [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover], async (security, actor) =>
        {
            var body = await ReadAsync<DefinitionBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            var definition = body.ToModel(name, tenant.Value);
            await service.SaveDefinitionAsync(definition, actor, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { name, status = "Active", hash = definition.ComputeDefinitionHash() });
        });

    internal static Task<IResult> DeleteDefinitionAsync(string name, HttpContext context, CallerAttributeAdministrationService service) =>
        ExecuteAsync(context, [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover], async (security, actor) =>
        {
            var tenant = ResolveTenant(security, context.Request.Query["tenant"]);
            if (tenant == null)
            {
                return Forbidden();
            }

            await service.DeleteDefinitionAsync(tenant.Value, name, actor, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { name, status = "Deleted" });
        });

    // ---- service sets ----------------------------------------------------------------------------------------------------------------

    internal static async Task<IResult> ListServiceSetsAsync(HttpContext context, CallerAttributeAdministrationService service)
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
            service_sets = snapshot.ServiceSets.Where(s => all || s.Set.Tenant == security.TenantId).Select(s => new
            {
                tenant = s.Set.Tenant.Value,
                application_id = s.Set.ApplicationId,
                attribute = s.Set.AttributeName,
                status = s.Set.Status.ToString(),
                hash = s.Set.ComputeValuesHash(),
                value_count = s.Set.Values.Count,
                created_by = s.CreatedBy,
                approved_by = s.ApprovedBy
            })
        });
    }

    internal static Task<IResult> PutServiceSetAsync(HttpContext context, CallerAttributeAdministrationService service) =>
        ExecuteAsync(context, [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover], async (security, actor) =>
        {
            var body = await ReadAsync<ServiceSetBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            await service.SaveServiceSetAsync(tenant.Value, body.ApplicationId ?? string.Empty, body.Attribute ?? string.Empty, body.Values ?? [], actor, context.RequestAborted).ConfigureAwait(false);
            var applied = new ServiceAttributeSet(Guid.Empty, tenant.Value, CallerAttributeNameRules.CanonicalApplicationId(body.ApplicationId ?? string.Empty), body.Attribute ?? string.Empty, body.Values ?? [], FilterApprovalStatus.Active);
            return Results.Ok(new { status = "Active", hash = applied.ComputeValuesHash() });
        });

    // ---- simulation ------------------------------------------------------------------------------------------------------------------

    internal static async Task<IResult> SimulateAsync(HttpContext context, CallerAttributeSimulationService simulation)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.FilterAdmin))
        {
            return Forbidden();
        }

        try
        {
            var body = await ReadAsync<SimulationBody>(context).ConfigureAwait(false);
            var tenant = ResolveTenant(security, body.Tenant);
            if (tenant == null)
            {
                return Forbidden();
            }

            SubjectKeyKind? kind = null;
            if (!string.IsNullOrWhiteSpace(body.KeyKind))
            {
                if (!Enum.TryParse<SubjectKeyKind>(body.KeyKind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    throw new ArgumentException("The subject key kind is not valid.");
                }

                kind = parsed;
            }

            var items = await simulation.SimulateAsync(
                new CallerAttributeSimulationRequest(tenant.Value, kind, body.KeyValue, body.Issuer, body.ApplicationId, body.Attributes ?? [], body.Reveal, body.Reason),
                security.UserSid.Value,
                security.TenantId,
                security.HasRole(GatewayRole.CallerAttributeInspector),
                security.IsClusterAdmin,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new
            {
                attributes = items.Select(i => new
                {
                    attribute = i.Attribute,
                    outcome = i.Outcome.ToString(),
                    reason = i.ReasonCode,
                    count = i.Count,
                    value_hashes = i.ValueHashes,
                    values = i.Values
                })
            });
        }
        catch (CallerAttributeSimulationException ex)
        {
            int status = ex.Code switch
            {
                "RateLimited" => StatusCodes.Status429TooManyRequests,
                "RevealRoleRequired" or "TenantMismatch" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest
            };
            return SafeErrorResults.Problem(status, "CALLER_ATTRIBUTE_SIMULATION_" + ex.Code.ToUpperInvariant(), "The simulation was refused.", context);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            SafeErrorResults.LogFailure(context, ex, "CALLER_ATTRIBUTE_INVALID_REQUEST");
            return SafeErrorResults.BadRequest("CALLER_ATTRIBUTE_INVALID_REQUEST", "The request is invalid.", context);
        }
    }

    // ---- plumbing --------------------------------------------------------------------------------------------------------------------

    private static bool CanRead(SecurityPrincipalContext security) =>
        security.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.FilterAdmin, GatewayRole.CallerAttributeApprover);

    private static async Task<IResult> ExecuteAsync(
        HttpContext context, GatewayRole[] anyOf, Func<SecurityPrincipalContext, CallerAttributeActor, Task<IResult>> action)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasAnyRole(anyOf))
        {
            var audit = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
            await audit.RecordRequiredAsync(new AuditLogEntry
            {
                EventType = "CALLER_ATTRIBUTE_ADMIN_DENIED",
                TargetTable = context.Request.Path.Value ?? "caller_attribute",
                ActorSid = security.UserSid,
                TenantId = security.TenantId,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { error = "Forbidden: missing required role" }),
                OccurredAt = DateTimeOffset.UtcNow
            }, context.RequestAborted).ConfigureAwait(false);
            return Forbidden();
        }

        if (string.IsNullOrWhiteSpace(security.UserSid.Value))
        {
            return Forbidden();
        }

        // Every identifier form of the caller (subject key, app/client ids, UPN, mail, object id) and the caller's groups feed the self-widening check.
        var identifiers = context.User.GetUserIdentifiers();
        identifiers.Add(security.UserSid.Value);
        AddApplicationIdentifiers(identifiers, context.User, security.UserSid.Value);
        var actor = new CallerAttributeActor(
            security.UserSid.Value,
            security.HasRole(GatewayRole.CallerAttributeApprover),
            identifiers,
            security.GroupSids.Select(g => g.Value).ToArray());
        try
        {
            return await action(security, actor).ConfigureAwait(false);
        }
        catch (CallerAttributeAdminException ex)
        {
            int status = ex.Code switch
            {
                nameof(CallerAttributeChangeResult.NotFound) => StatusCodes.Status404NotFound,
                "ApproverRoleRequired" or "SelfWidening" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest
            };
            return SafeErrorResults.Problem(status, "CALLER_ATTRIBUTE_" + ex.Code.ToUpperInvariant(), "The caller attribute change was refused.", context);
        }
        catch (SingleApprovalRefusedException ex)
        {
            int status = ex.Code == "AuditFailed" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden;
            return SafeErrorResults.Problem(status, "CALLER_ATTRIBUTE_" + ex.Code.ToUpperInvariant(), "The caller attribute change was refused.", context);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            SafeErrorResults.LogFailure(context, ex, "CALLER_ATTRIBUTE_INVALID_REQUEST");
            return SafeErrorResults.BadRequest("CALLER_ATTRIBUTE_INVALID_REQUEST", "The request is invalid.", context);
        }
    }

    private static readonly string[] AppIdClaimTypes = ["appid", "azp", "client_id"];

    private static void AddApplicationIdentifiers(
        HashSet<string> identifiers, System.Security.Claims.ClaimsPrincipal principal, string userSid)
    {
        static void AddId(HashSet<string> set, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            set.Add(trimmed);
            set.Add(CallerAttributeNameRules.CanonicalApplicationId(trimmed));
        }

        static void AddRealmSuffix(HashSet<string> set, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var pipe = raw.IndexOf('|', StringComparison.Ordinal);
            if (pipe >= 0 && pipe + 1 < raw.Length)
            {
                AddId(set, raw[(pipe + 1)..]);
            }
        }

        AddId(identifiers, AppOnlyPrincipal.GetClientId(principal));
        foreach (var type in AppIdClaimTypes)
        {
            foreach (var claim in principal.FindAll(type))
            {
                AddId(identifiers, claim.Value);
            }
        }

        AddRealmSuffix(identifiers, userSid);
        foreach (var claim in principal.FindAll(System.Security.Claims.ClaimTypes.PrimarySid))
        {
            AddRealmSuffix(identifiers, claim.Value);
        }
    }

    private static TenantId? ResolveTenant(SecurityPrincipalContext security, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, security.TenantId.Value, StringComparison.Ordinal))
        {
            return security.TenantId;
        }

        return security.IsClusterAdmin ? new TenantId(requested) : (TenantId?)null;
    }

    private static async Task<T> ReadAsync<T>(HttpContext context) =>
        await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false)
        ?? throw new ArgumentException("The request body is empty.");

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private sealed class DefinitionBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }

        [JsonPropertyName("value_type")] public string? ValueType { get; set; }

        [JsonPropertyName("cardinality")] public string? Cardinality { get; set; }

        [JsonPropertyName("max_values")] public int? MaxValues { get; set; }

        [JsonPropertyName("ttl_seconds")] public int? TtlSeconds { get; set; }

        [JsonPropertyName("provider")] public ProviderBody? Provider { get; set; }

        [JsonPropertyName("description")] public string? Description { get; set; }

        public CallerAttributeDefinition ToModel(string name, TenantId tenant)
        {
            if (!Enum.TryParse<CallerAttributeType>(ValueType, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            {
                throw new ArgumentException("The value type is not valid (Text, Whole, Number, Flag, Uuid).");
            }

            var cardinality = CallerAttributeCardinality.Scalar;
            if (!string.IsNullOrWhiteSpace(Cardinality) && (!Enum.TryParse(Cardinality, ignoreCase: true, out cardinality) || !Enum.IsDefined(cardinality)))
            {
                throw new ArgumentException("The cardinality is not valid (Scalar, List).");
            }

            if (Provider is null || string.IsNullOrWhiteSpace(Provider.Kind))
            {
                throw new ArgumentException("The provider kind is required.");
            }

            return new CallerAttributeDefinition(
                Guid.NewGuid(), tenant, name, type, cardinality,
                MaxValues ?? (cardinality == CallerAttributeCardinality.Scalar ? 1 : CallerAttributeDefinition.DefaultMaxValues),
                TtlSeconds ?? CallerAttributeDefinition.DefaultTtlSeconds,
                new CallerAttributeProviderBinding(Provider.Kind, new Dictionary<string, string>(Provider.Config ?? [], StringComparer.Ordinal)),
                Description, FilterApprovalStatus.Active);
        }
    }

    private sealed class ProviderBody
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }

        [JsonPropertyName("config")] public Dictionary<string, string>? Config { get; set; }
    }

    private sealed class ServiceSetBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }

        [JsonPropertyName("application_id")] public string? ApplicationId { get; set; }

        [JsonPropertyName("attribute")] public string? Attribute { get; set; }

        [JsonPropertyName("values")] public List<string>? Values { get; set; }

    }

    private sealed class SimulationBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }

        [JsonPropertyName("key_kind")] public string? KeyKind { get; set; }

        [JsonPropertyName("key_value")] public string? KeyValue { get; set; }

        [JsonPropertyName("issuer")] public string? Issuer { get; set; }

        [JsonPropertyName("application_id")] public string? ApplicationId { get; set; }

        [JsonPropertyName("attributes")] public List<string>? Attributes { get; set; }

        [JsonPropertyName("reveal")] public bool Reveal { get; set; }

        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
