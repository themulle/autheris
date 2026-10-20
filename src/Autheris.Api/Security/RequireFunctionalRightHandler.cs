namespace Autheris.Api.Security;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;

public sealed class RequireFunctionalRightHandler : AuthorizationHandler<RequireFunctionalRightRequirement>
{
    public const string FunctionalRightClaimType = "autheris:functional_right";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RequireFunctionalRightRequirement requirement)
    {
        if (context.User.HasClaim(c =>
            string.Equals(c.Type, FunctionalRightClaimType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Value, requirement.Right.Name, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
