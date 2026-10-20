namespace Autheris.Api.Security;

using System;
using Autheris.Domain.Governance;
using Microsoft.AspNetCore.Authorization;

public sealed class RequireFunctionalRightRequirement : IAuthorizationRequirement
{
    public FunctionalRight Right { get; }

    public RequireFunctionalRightRequirement(FunctionalRight right)
    {
        Right = right;
    }

    public RequireFunctionalRightRequirement(string rightName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rightName);
        Right = new FunctionalRight(rightName);
    }
}
