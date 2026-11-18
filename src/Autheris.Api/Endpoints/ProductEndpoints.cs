namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Data products: administration API. Creation authority is decided by <see cref="ProductAuthorityService"/> (ownership of every
/// base object, data-source ownership, <c>DataProductAdmin</c>); the product owner only manages its own product. The sync role is limited
/// to the sync endpoints and is refused here. Callers stay within their tenant.
/// </summary>
public static partial class ProductEndpoints
{
    private const int MaxBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1/products")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductDefinitionChanged);

        group.MapGet("/", (HttpContext context, DataProductService service) => ListAsync(context, service))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapGet("{schema}/{name}", (string schema, string name, HttpContext context, DataProductService service) => GetAsync(schema, name, context, service))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPut("{schema}/{name}", (string schema, string name, HttpContext context, DataProductService service) => PutAsync(schema, name, context, service))
            .WithRequestBodyLimit(MaxBodyBytes);
        group.MapPost("{schema}/{name}/owner", (string schema, string name, HttpContext context, DataProductService service) => TransferAsync(schema, name, context, service))
            .WithRequestBodyLimit(MaxBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductOwnerTransferred);
        group.MapPost("{schema}/{name}/retire", (string schema, string name, HttpContext context, DataProductService service) => RetireAsync(schema, name, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductRetired);
        group.MapPost("{schema}/{name}/settings", (string schema, string name, HttpContext context, DataProductService service) => SettingsAsync(schema, name, context, service))
            .WithRequestBodyLimit(MaxBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductSettingsChanged);
        group.MapPost("{schema}/{name}/columns/{column}/classification", (string schema, string name, string column, HttpContext context, DataProductService service) => ClassifyAsync(schema, name, column, context, service))
            .WithRequestBodyLimit(MaxBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductColumnClassificationChanged);

        group.MapGet("sources/{dataSource}/owners", (string dataSource, HttpContext context, DataSourceOwnerService service) => ListOwnersAsync(dataSource, context, service))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPut("sources/{dataSource}/owners/{ownerSid}", (string dataSource, string ownerSid, HttpContext context, DataSourceOwnerService service) => AssignOwnerAsync(dataSource, ownerSid, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.DataSourceOwnerAssigned);
        group.MapDelete("sources/{dataSource}/owners/{ownerSid}", (string dataSource, string ownerSid, HttpContext context, DataSourceOwnerService service) => RemoveOwnerAsync(dataSource, ownerSid, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.DataSourceOwnerRemoved);

        MapSyncRoutes(app, group);

        return app;
    }

    internal sealed record LoadBody(LoadMode Mode, int TtlMinutes = 0, int? IntervalMinutes = null, int IdleDropMinutes = 0, int? MaxStalenessMinutes = null)
    {
        public LoadSettings ToModel() => new(Mode, TtlMinutes, IntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null, IdleDropMinutes, MaxStalenessMinutes);
    }

    internal sealed record LimitsBody(long MaxRows = 0, long MaxBytes = 0, int MaxRuntimeSeconds = 0);

    internal sealed record PutBody(DataProductKind Kind, string? Definition, string? ProductOwner, LoadBody? Load, LimitsBody? Limits, string? Description);

    internal sealed record OwnerBody(string? Owner);

    internal sealed record SettingsBody(string? Description, LoadBody? Load);

    internal sealed record ClassifyBody(ProductColumnClass Class, string? Reason);

    /// <summary>The caller of a product route, or null when it must be refused (the sync role is limited to the sync endpoints).</summary>
    private static ProductCaller? CallerOf(SecurityPrincipalContext security) =>
        ProductAuthorityService.ForRestRoute(
            security.UserSid,
            security.TenantId,
            security.HasRole(GatewayRole.DataProductAdmin),
            security.HasRole(GatewayRole.DataProductSync),
            security.HasRole(GatewayRole.SecurityAuditor));

    private static async Task<IResult> RunAsync(HttpContext context, Func<ProductCaller, Task<IResult>> action)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        var caller = CallerOf(security);
        if (caller is null)
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, "The synchronisation identity may only call the synchronisation endpoints.", context);
        }

        return await MapErrorsAsync(() => action(caller), context).ConfigureAwait(false);
    }

    private static async Task<IResult> MapErrorsAsync(Func<Task<IResult>> action, HttpContext context)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ProductException ex)
        {
            return ex.Failure switch
            {
                ProductFailure.NotFound => ApiProblems.NotFound(ex.Code, "The data product was not found.", context),
                ProductFailure.Conflict => ApiProblems.Conflict(ex.Code, "The data product operation conflicts with its current state.", context),
                ProductFailure.Invalid => ApiProblems.Unprocessable(ex.Code, "The data product request is invalid.", context),
                _ when ex.Code == ProductErrorCodes.OwnerMustDiffer => ApiProblems.Unprocessable(ex.Code, "The product owner must differ from the creator.", context),
                _ => ApiProblems.Forbidden(ex.Code, "The caller is not allowed to perform this data product operation.", context)
            };
        }
        catch (ProductSyncRejectedException ex)
        {
            return ApiProblems.Unprocessable(
                ProductSyncRejectedException.StableCode,
                $"The commit was rejected ({ex.Reason}{(ex.FileIndex >= 0 ? $", file {ex.FileIndex}" : string.Empty)}{(ex.ProductCode is null ? string.Empty : $", {ex.ProductCode}")}); nothing was applied.",
                context);
        }
        catch (JsonException ex)
        {
            ApiProblems.LogFailure(context, ex, ApiErrorCodes.InvalidJson);
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidJson, "The request body is not valid JSON.", context);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            ApiProblems.LogFailure(context, ex, ApiErrorCodes.BadRequest);
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The request is invalid.", context);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpContext context) =>
        await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false)
        ?? throw new ArgumentException("The request body is empty.");

    internal static object View(DataProduct p) => new
    {
        id = p.Id,
        schema = p.Schema,
        name = p.Name,
        kind = p.Kind.ToString(),
        definition = p.Definition,
        definition_hash = p.DefinitionHash,
        base_objects = p.BaseObjects.Select(static o => o.ToString()),
        columns = p.Columns.Select(static c => new
        {
            name = c.Name,
            data_type = c.DataType,
            @class = c.Class.ToString(),
            lineage = c.Lineage.Select(static l => new { table = l.Table.ToString(), column = l.Column }),
            declassified_by = c.DeclassifiedBy,
            declassification_reason = c.DeclassificationReason
        }),
        created_by = p.CreatedBy.Value,
        authority = p.Authority.ToString(),
        status = p.Status.ToString(),
        managed_by = p.ManagedBy,
        load = p.Load,
        limits = p.Limits,
        description = p.Description,
        product_owner = p.ProductOwner.Value,
        version = p.Version,
        created_at = p.CreatedAt,
        updated_at = p.UpdatedAt
    };
}
