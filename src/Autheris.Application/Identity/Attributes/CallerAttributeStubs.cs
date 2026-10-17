namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using TrinoSqlEngine.Governance;

/// <summary>Contract stub (WP-4): reserved parameter names of caller attribute slots and values.</summary>
public static class CallerAttributeParameterName
{
    public const string Prefix = "__gql_caller_";

    public static bool TryParse(string name, out string attribute) => throw new NotImplementedException("WP-4");
}

/// <summary>Contract stub (WP-4): padding of expanded lists (A-6).</summary>
public sealed record CallerAttributePadding(bool PadToBuckets, int MaxValues)
{
    public static CallerAttributePadding Default { get; } = new(true, 200);
}

/// <summary>Contract stub (WP-4): the only code that constructs a valued <c>__gql_caller_</c> parameter (ST-129-14).</summary>
public static class CallerAttributeBinder
{
    public static PolicyPredicate Bind(PolicyPredicate predicate, CallerAttributeSet set, CallerAttributePadding padding)
        => throw new NotImplementedException("WP-4");
}
