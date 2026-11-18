namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public static partial class ProductEndpoints
{
    internal static Task<IResult> ListAsync(HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            int limit = int.TryParse(context.Request.Query["limit"], out var parsed) ? parsed : 50;
            var items = await service.ListAsync(caller, context.Request.Query["after"], limit, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { items = items.Select(View), next_after = items.Count == 0 ? null : $"{items[^1].Schema}/{items[^1].Name}" });
        });

    internal static Task<IResult> GetAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller => Results.Ok(View(await service.GetAsync(caller, schema, name, context.RequestAborted).ConfigureAwait(false))));

    internal static Task<IResult> PutAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            var body = await ReadAsync<PutBody>(context).ConfigureAwait(false);
            var owner = string.IsNullOrWhiteSpace(body.ProductOwner) ? (Sid?)null : new Sid(body.ProductOwner.Trim());
            var saved = await service.PutAsync(
                new ProductPutRequest(
                    caller, schema, name, body.Kind, body.Definition ?? string.Empty, owner, body.Load?.ToModel(),
                    body.Limits is null ? null : new Autheris.Domain.Model.ProductLimits(body.Limits.MaxRows, body.Limits.MaxBytes, body.Limits.MaxRuntimeSeconds),
                    body.Description),
                context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(View(saved));
        });

    internal static Task<IResult> TransferAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            var body = await ReadAsync<OwnerBody>(context).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.Owner))
            {
                throw new ArgumentException("The new owner is required.");
            }

            return Results.Ok(View(await service.TransferAsync(caller, schema, name, new Sid(body.Owner.Trim()), context.RequestAborted).ConfigureAwait(false)));
        });

    internal static Task<IResult> RetireAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller => Results.Ok(View(await service.RetireAsync(caller, schema, name, context.RequestAborted).ConfigureAwait(false))));

    internal static Task<IResult> SettingsAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            var body = await ReadAsync<SettingsBody>(context).ConfigureAwait(false);
            return Results.Ok(View(await service.UpdateSettingsAsync(caller, schema, name, body.Description, body.Load?.ToModel(), context.RequestAborted).ConfigureAwait(false)));
        });

    internal static Task<IResult> ClassifyAsync(string schema, string name, string column, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            var body = await ReadAsync<ClassifyBody>(context).ConfigureAwait(false);
            return Results.Ok(View(await service.ClassifyColumnAsync(caller, schema, name, column, body.Class, body.Reason, context.RequestAborted).ConfigureAwait(false)));
        });

    private static bool CanAssignOwners(SecurityPrincipalContext security) =>
        security.HasRole(GatewayRole.GovernanceAdmin) || security.HasRole(GatewayRole.TenantAdmin);

    internal static async Task<IResult> ListOwnersAsync(string dataSource, HttpContext context, DataSourceOwnerService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanAssignOwners(security) && !security.HasRole(GatewayRole.SecurityAuditor) && !security.HasRole(GatewayRole.DataProductAdmin))
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, httpContext: context);
        }

        return await MapErrorsAsync(async () =>
        {
            var owners = await service.ListAsync(security.TenantId, dataSource, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { owners = owners.Select(static o => new { owner = o.OwnerSid.Value, assigned_by = o.AssignedBy.Value, assigned_at = o.AssignedAt }) });
        }, context).ConfigureAwait(false);
    }

    internal static async Task<IResult> AssignOwnerAsync(string dataSource, string ownerSid, HttpContext context, DataSourceOwnerService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanAssignOwners(security))
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, httpContext: context);
        }

        return await MapErrorsAsync(async () =>
        {
            var added = await service.AssignAsync(security.TenantId, security.UserSid, dataSource, new Sid(ownerSid), context.RequestAborted).ConfigureAwait(false);
            _ = added; // assigning an already assigned owner is idempotent
            return Results.NoContent();
        }, context).ConfigureAwait(false);
    }

    internal static async Task<IResult> RemoveOwnerAsync(string dataSource, string ownerSid, HttpContext context, DataSourceOwnerService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanAssignOwners(security))
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, httpContext: context);
        }

        return await MapErrorsAsync(async () =>
        {
            var removed = await service.RemoveAsync(security.TenantId, security.UserSid, dataSource, new Sid(ownerSid), context.RequestAborted).ConfigureAwait(false);
            return removed ? Results.NoContent() : ApiProblems.NotFound(ProductErrorCodes.NotFound, "The data source owner assignment was not found.", context);
        }, context).ConfigureAwait(false);
    }
}
