namespace Autheris.Application.Usage;

using System;

public readonly record struct GrantRef(string GrantId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil)
{
    public static readonly GrantRef Invalid = new("invalid", null, null);
}
