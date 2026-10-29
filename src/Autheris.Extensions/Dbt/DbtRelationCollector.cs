namespace Autheris.Extensions.Dbt;

using System.Collections.Generic;
using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Model;

/// <summary>Collects relation definitions and reason-coded warnings with the SEC-REL151-02 limits.</summary>
internal sealed class RelationCollector
{
    public const int MaxRelations = 10_000;

    private readonly List<DbtRelationshipDefinition> _relationships = [];
    private readonly List<string> _warnings = [];
    /// <summary>Total <c>depends_on</c> ids held by pending relations per manifest (bounds memory: 10 000 relations x 1 024 ids at worst).</summary>
    public const int MaxPendingDependsOn = 500_000;

    private int _dependsOnBudget = MaxPendingDependsOn;
    private int _attempts;
    private bool _overflow;

    public IReadOnlyList<DbtRelationshipDefinition> Relationships => _relationships;

    public IReadOnlyList<string> Finish()
    {
        if (_overflow)
        {
            _warnings.Add($"Relation import limited to {MaxRelations} relations per manifest; the rest were skipped: TOO_MANY_RELATIONS");
        }

        return _warnings;
    }

    /// <summary>Counts one relation attempt (SEC-REL151-02, I392-SC-08); false when the per-manifest limit is reached.</summary>
    public bool Reserve() => Count();

    /// <summary>Reserves <paramref name="count"/> held <c>depends_on</c> ids; false (and overflow) when the budget is exhausted.</summary>
    public bool ReserveDependsOn(int count)
    {
        if (count > _dependsOnBudget)
        {
            _overflow = true;
            return false;
        }

        _dependsOnBudget -= count;
        return true;
    }

    /// <summary>Adds a reason-coded warning that is not tied to a relation (counted like a relation attempt).</summary>
    public void Warn(string message)
    {
        if (Count())
        {
            _warnings.Add(message);
        }
    }

    public void Skip(string name, string child, string parent, string code, bool reserved = false)
    {
        if (!reserved && !Count())
        {
            return;
        }

        AddWarning(name, child, parent, code);
    }

    public void TryAdd(string name, string parent, string child, IReadOnlyList<string> parentColumns, IReadOnlyList<string> childColumns,
        bool reserved = false, string? parentUniqueId = null, string? childUniqueId = null)
    {
        if (!reserved && !Count())
        {
            return;
        }

        if (!DbtIdentifierSanitizer.IsValidIdentifier(name) || !DbtIdentifierSanitizer.IsValidIdentifier(parent) ||
            !DbtIdentifierSanitizer.IsValidIdentifier(child) || HasInvalid(parentColumns) || HasInvalid(childColumns))
        {
            AddWarning(name, child, parent, "INVALID_IDENTIFIER");
            return;
        }

        var violation = RelationKeyRules.Validate(parentColumns, childColumns);
        if (violation is not null)
        {
            AddWarning(name, child, parent, violation.Value switch
            {
                RelationKeyViolation.MissingColumns => "MISSING_COLUMNS",
                RelationKeyViolation.CountMismatch => "COUNT_MISMATCH",
                RelationKeyViolation.DuplicateColumn => "DUPLICATE_COLUMN",
                RelationKeyViolation.TooWide => "TOO_WIDE",
                _ => "INVALID_IDENTIFIER"
            });
            return;
        }

        _relationships.Add(new DbtRelationshipDefinition(name, parent, child, [.. parentColumns], [.. childColumns], parentUniqueId, childUniqueId));
    }

    private bool Count()
    {
        if (_attempts >= MaxRelations)
        {
            _overflow = true;
            return false;
        }

        _attempts++;
        return true;
    }

    private static bool HasInvalid(IReadOnlyList<string> columns)
    {
        foreach (var c in columns)
        {
            if (!DbtIdentifierSanitizer.IsValidIdentifier(c))
            {
                return true;
            }
        }

        return false;
    }

    private void AddWarning(string name, string child, string parent, string code) =>
        _warnings.Add($"Relation '{DbtIdentifierSanitizer.ForLog(name)}' ({DbtIdentifierSanitizer.ForLog(child)} -> {DbtIdentifierSanitizer.ForLog(parent)}) skipped: {code}");
}
