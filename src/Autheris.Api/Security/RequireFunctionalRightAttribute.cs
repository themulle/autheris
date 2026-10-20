namespace Autheris.Api.Security;

using System;
using Microsoft.AspNetCore.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireFunctionalRightAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "FunctionalRight:";

    public string RightName { get; }

    public RequireFunctionalRightAttribute(string rightName)
        : base(policy: $"{PolicyPrefix}{rightName}")
    {
        RightName = rightName;
    }
}
