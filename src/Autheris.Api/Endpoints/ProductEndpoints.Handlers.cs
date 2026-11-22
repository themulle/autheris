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
            var ownerGroup = string.IsNullOrWhiteSpace(body.OwnerGroupSid) ? (Sid?)null : new Sid(body.OwnerGroupSid.Trim());
            var saved = await service.PutAsync(
                new ProductPutRequest(
                    caller, schema, name, body.Kind, body.Definition ?? string.Empty, ownerGroup, body.Load?.ToModel(),
                    body.Limits is null ? null : new Autheris.Domain.Model.ProductLimits(body.Limits.MaxRows, body.Limits.MaxBytes, body.Limits.MaxRuntimeSeconds),
                    body.Description,
                    DefaultClass: body.DefaultClass,
                    Visibility: body.Visibility),
                context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(View(saved));
        });

    internal static Task<IResult> SetOwnerGroupAsync(string schema, string name, HttpContext context, DataProductService service) =>
        RunAsync(context, async caller =>
        {
            var body = await ReadAsync<OwnerGroupBody>(context).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.OwnerGroupSid))
            {
                throw new ArgumentException("The owner group SID is required.");
            }

            return Results.Ok(View(await service.SetOwnerGroupAsync(caller, schema, name, new Sid(body.OwnerGroupSid.Trim()), context.RequestAborted).ConfigureAwait(false)));
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
}
