namespace Autheris.GraphQL.Directives;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class RebacDirective
{
    public string Relation { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string? ObjectArg { get; set; }
}

public sealed class RebacDirectiveType : DirectiveType<RebacDirective>
{
    protected override void Configure(IDirectiveTypeDescriptor<RebacDirective> descriptor)
    {
        descriptor.Name("rebac");
        descriptor.Description("Enforces Zanzibar Relationship-Based Access Control (ReBAC) on the field before resolver execution.");
        descriptor.Location(HotChocolate.Types.DirectiveLocation.FieldDefinition);
        descriptor.Argument(t => t.Relation).Type<NonNullType<StringType>>().Description("Required relation, e.g. viewer, editor, owner.");
        descriptor.Argument(t => t.ObjectType).Type<NonNullType<StringType>>().Description("Target object type, e.g. document, dataset, folder.");
        descriptor.Argument(t => t.ObjectArg).Type<StringType>().Description("Name of the GraphQL field argument containing the object ID. Defaults to 'id'.");
        descriptor.Use<RebacFieldMiddleware>();
    }
}

public sealed class RebacFieldMiddleware
{
    private readonly FieldDelegate _next;

    public RebacFieldMiddleware(FieldDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(IMiddlewareContext context)
    {
        var options = context.Services.GetService<IOptions<GatewayOptions>>()?.Value?.Rebac;
        if (options is not null && !options.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var directive = context.Selection.Field.Directives.FirstOrDefault(d => string.Equals(d.Type.Name, "rebac", StringComparison.OrdinalIgnoreCase));
        if (directive == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var rebacConfig = directive.ToValue<RebacDirective>();
        var relation = rebacConfig.Relation;
        var objectType = rebacConfig.ObjectType;
        var objectArg = string.IsNullOrWhiteSpace(rebacConfig.ObjectArg) ? "id" : rebacConfig.ObjectArg;

        // 1. Resolve Target Object ID
        string? objectId = null;
        var hasArg = context.Selection.Field.Arguments.Any(a => string.Equals(a.Name, objectArg, StringComparison.OrdinalIgnoreCase));
        if (hasArg)
        {
            try
            {
                objectId = context.ArgumentValue<string?>(objectArg);
            }
            catch
            {
                var raw = context.ArgumentValue<object?>(objectArg);
                objectId = raw?.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(objectId))
        {
            // Fail-closed: Cannot identify target object
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("Access denied: Target object ID could not be resolved for ReBAC evaluation.")
                    .SetCode("AUTH_NOT_AUTHORIZED")
                    .SetPath(context.Path)
                    .Build());
            return;
        }

        // 2. Resolve Principal & Caller Identity
        var httpContext = context.Services.GetService<IHttpContextAccessor>()?.HttpContext
                          ?? (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc ? hc : null);

        ClaimsPrincipal? principal = httpContext?.User
            ?? (context.ContextData.TryGetValue("ClaimsPrincipal", out var cpObj) && cpObj is ClaimsPrincipal cp ? cp : null);

        var identity = principal?.Identity;
        if (identity == null || !identity.IsAuthenticated)
        {
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("Access denied: User is not authenticated.")
                    .SetCode("AUTH_NOT_AUTHENTICATED")
                    .SetPath(context.Path)
                    .Build());
            return;
        }

        var caller = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? principal?.FindFirst("sub")?.Value
                     ?? principal?.FindFirst(ClaimTypes.Name)?.Value
                     ?? identity.Name;

        if (string.IsNullOrWhiteSpace(caller))
        {
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("Access denied: Caller identity missing.")
                    .SetCode("AUTH_NOT_AUTHENTICATED")
                    .SetPath(context.Path)
                    .Build());
            return;
        }

        // 3. Resolve Tenant (Priority: Middleware-validated TenantId -> Token Claim -> Fallback)
        var tenantStr = (httpContext != null && httpContext.Items.TryGetValue("TenantId", out var itemT) && itemT is TenantId tid ? tid.Value : null)
                        ?? principal?.FindFirst("tenant_id")?.Value
                        ?? principal?.FindFirst("tid")?.Value
                        ?? principal?.FindFirst("tenant")?.Value
                        ?? TenantId.LegacySingleTenant.Value;

        var tenantId = new TenantId(tenantStr);
        var targetObject = $"{objectType}:{objectId}";

        // 4. Batch DataLoader check (Zero-N+1)
        var dataLoader = context.Services.GetRequiredService<IRebacBatchDataLoader>();
        var isAllowed = await dataLoader.CheckAsync(tenantId.Value, caller, relation, targetObject, context.RequestAborted).ConfigureAwait(false);

        if (!isAllowed)
        {
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("Access denied by ReBAC policy.")
                    .SetCode("AUTH_NOT_AUTHORIZED")
                    .SetPath(context.Path)
                    .Build());
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
