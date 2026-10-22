namespace Autheris.GraphQL.Federation;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Options;
using HotChocolate.Configuration;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors.Configurations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// GQL-15: Intercepts object types and attaches leaf field masking middleware to enforce
/// federated in-memory column and PII data masking on HotChocolate 16.6.8.
/// </summary>
public sealed class SubgraphFieldMaskingTypeInterceptor : TypeInterceptor
{
    private const string MaskContextKey = "Autheris.GraphQL.Federation.MaskContext";
    private const string MaskAdminKey = "Autheris.GraphQL.Federation.MaskIsAdmin";

    public override void OnBeforeCompleteType(ITypeCompletionContext completionContext, TypeSystemConfiguration configuration)
    {
        if (configuration is ObjectTypeConfiguration objType)
        {
            if (!string.IsNullOrEmpty(objType.Name) && objType.Name.StartsWith("__", StringComparison.Ordinal))
            {
                return;
            }

            var middleware = CreateMiddleware();
            foreach (var field in objType.Fields)
            {
                if (field.IsIntrospectionField)
                {
                    continue;
                }

                field.MiddlewareConfigurations.Add(new FieldMiddlewareConfiguration(
                    middleware,
                    isRepeatable: false,
                    key: "SubgraphFieldMasking"));
            }
        }
    }

    private static FieldMiddleware CreateMiddleware()
    {
        return next => async context =>
        {
            await next(context).ConfigureAwait(false);

            if (context.Result == null)
            {
                return;
            }

            var namedType = context.Selection.Field.Type.NamedType();
            if (!namedType.IsLeafType())
            {
                return;
            }

            var options = context.Services.GetService<IOptions<GatewayOptions>>();
            if (options?.Value?.Federation?.EnableResultMasking != true || options.Value.IsColumnMaskingDisabled)
            {
                return;
            }

            var masker = context.Services.GetService<ISubgraphResultMasker>();
            if (masker == null)
            {
                return;
            }

            if (!context.ContextData.TryGetValue(MaskAdminKey, out var isAdminObj))
            {
                var principal = ResolvePrincipal(context);
                var isAdmin = masker.IsAdmin(principal);
                context.ContextData[MaskAdminKey] = isAdmin;
                if (isAdmin)
                {
                    return;
                }
            }
            else if (isAdminObj is true)
            {
                return;
            }

            if (!context.ContextData.TryGetValue(MaskContextKey, out var maskCtxObj) || maskCtxObj is not SubgraphResultMasker.MaskContext maskCtx)
            {
                var principal = ResolvePrincipal(context);
                maskCtx = await masker.CreateMaskContextAsync(principal, context.RequestAborted).ConfigureAwait(false);
                context.ContextData[MaskContextKey] = maskCtx;
            }

            var responseName = context.Selection.ResponseName;
            var fieldName = context.Selection.Field.Name;
            var parentType = context.ObjectType.Name;

            var maskedValue = masker.MaskLeaf(responseName, fieldName, parentType, context.Result, maskCtx, out var wasMasked);
            if (wasMasked)
            {
                context.Result = maskedValue;
                context.SetGlobalState("MaskingApplied", true);
                if (context.Services.GetService<IHttpContextAccessor>()?.HttpContext is { } httpContext)
                {
                    httpContext.Items["MaskingApplied"] = true;
                }
            }
        };
    }

    private static ClaimsPrincipal? ResolvePrincipal(IResolverContext context)
    {
        if (context.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cpObj) && cpObj is ClaimsPrincipal cp)
        {
            return cp;
        }
        if (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpStrObj) && cpStrObj is ClaimsPrincipal cpStr)
        {
            return cpStr;
        }
        return context.Services.GetService<IHttpContextAccessor>()?.HttpContext?.User;
    }
}
