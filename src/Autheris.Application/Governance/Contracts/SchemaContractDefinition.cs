namespace Autheris.Application.Governance.Contracts;

using System;
using System.Collections.Generic;

/// <summary>
/// F-GOV-08: Defines a schema contract slice based on @tag inclusion/exclusion and @inaccessible rules.
/// </summary>
public sealed class SchemaContractDefinition
{
    public string Name { get; init; } = string.Empty;
    public HashSet<string> IncludedTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedTables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExcludeInaccessible { get; init; } = true;

    public SchemaContractDefinition() { }

    public SchemaContractDefinition(
        string name,
        IEnumerable<string>? includedTags = null,
        IEnumerable<string>? excludedTags = null,
        bool excludeInaccessible = true,
        IEnumerable<string>? allowedTables = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (includedTags != null)
        {
            foreach (var t in includedTags) IncludedTags.Add(t);
        }
        if (excludedTags != null)
        {
            foreach (var t in excludedTags) ExcludedTags.Add(t);
        }
        if (allowedTables != null)
        {
            foreach (var tbl in allowedTables) AllowedTables.Add(tbl);
        }
        ExcludeInaccessible = excludeInaccessible;
    }
}
