namespace Autheris.Application.VirtualFilters;

using System;

/// <summary>
/// Placeholder until a dialect can render virtual filters: every applying filter fails, so the resolver denies
/// (fail closed) instead of letting rows through unfiltered.
/// </summary>
public sealed class UnavailablePredicateBuilder : IVirtualFilterPredicateBuilder
{
    public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) =>
        throw new NotSupportedException($"Virtual filters cannot be rendered for {dialect} yet.");
}
