namespace Autheris.Application.Governance.Contracts;

using System;
using System.Collections.Generic;
using Autheris.Domain.Security;

/// <summary>
/// Defines a schema contract slice based on protection classes, table allowlists, and <c>@inaccessible</c> rules.
/// </summary>
public sealed class SchemaContractDefinition
{
    public string Name { get; init; } = string.Empty;
    public HashSet<ProtectionClass> IncludedClasses { get; init; } = [];
    public HashSet<ProtectionClass> ExcludedClasses { get; init; } = [];
    public HashSet<string> AllowedTables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExcludeInaccessible { get; init; } = true;

    public SchemaContractDefinition() { }

    public SchemaContractDefinition(
        string name,
        IEnumerable<ProtectionClass>? includedClasses = null,
        IEnumerable<ProtectionClass>? excludedClasses = null,
        bool excludeInaccessible = true,
        IEnumerable<string>? allowedTables = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (includedClasses != null)
        {
            foreach (var c in includedClasses) IncludedClasses.Add(c.NormalizeOrConfidential());
        }
        if (excludedClasses != null)
        {
            foreach (var c in excludedClasses) ExcludedClasses.Add(c.NormalizeOrConfidential());
        }
        if (allowedTables != null)
        {
            foreach (var tbl in allowedTables) AllowedTables.Add(tbl);
        }
        ExcludeInaccessible = excludeInaccessible;
    }
}
