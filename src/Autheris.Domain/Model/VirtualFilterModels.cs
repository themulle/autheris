namespace Autheris.Domain.Model;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Approval status for virtual filters and access profiles when RequireApproval is active.</summary>
public enum FilterApprovalStatus
{
    Active = 0,
    PendingApproval = 1,
    Rejected = 2
}

/// <summary>What an object in a profile's scope that no binding covers means for the grantee.</summary>
public enum UncoveredPolicy
{
    /// <summary>The object is denied for the grantees (fail closed).</summary>
    Deny = 1,

    /// <summary>The object stays governed by consents alone.</summary>
    Skip = 2
}

/// <summary>Objects a binding applies to.</summary>
[Flags]
public enum FilterObjectKinds
{
    None = 0,

    /// <summary>Tables and views (the catalog does not distinguish them).</summary>
    Relation = 1,

    /// <summary>Result of a stored procedure (post-filtered through its row scope key).</summary>
    ProcedureResult = 2
}

public enum FilterConditionOperator
{
    Eq = 1,
    NotEq = 2,
    IsNull = 3,
    IsNotNull = 4
}

/// <summary>Join of the structured definition: <c>JOIN table AS alias ON left = right</c> (qualified <c>alias.column</c>).</summary>
public sealed record FilterJoin(TableIdentifier Table, string Alias, string LeftColumn, string RightColumn);

/// <summary>Condition of the structured definition: qualified column against a literal (or NULL test).</summary>
public sealed record FilterCondition(string Column, FilterConditionOperator Operator, string? Value = null);

/// <summary>Stage 1 definition: start table, join chain and equality / NULL conditions.</summary>
public sealed record StructuredFilterDefinition
{
    public TableIdentifier From { get; init; }
    public string FromAlias { get; init; } = string.Empty;
    public IReadOnlyList<FilterJoin> Joins { get; init; } = [];
    public IReadOnlyList<FilterCondition> Where { get; init; } = [];
}

/// <summary>Origin of a row managed by a file repository (Talos); such rows only change through the sync.</summary>
public sealed record ManagedBy(string Path, string Commit);

/// <summary>
/// A named, reusable virtual filter without reference to a protected table. It yields key values (and optionally a
/// validity window) the protected object must match. It never grants access.
/// </summary>
public sealed record VirtualFilter
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TenantId TenantId { get; init; } = TenantId.LegacySingleTenant;
    public string Name { get; init; } = string.Empty;

    /// <summary>Data source (catalog domain) of every table of the definition.</summary>
    public string Source { get; init; } = string.Empty;

    public StructuredFilterDefinition? Structured { get; init; }

    /// <summary>
    /// Stage 2 definition (design 3.7): <c>from ... [join ...] where ...</c> in Trino syntax, addressing the protected
    /// object as <c>target</c>. Exclusive with <see cref="Structured"/>.
    /// </summary>
    public string? Sql { get; init; }

    /// <summary>Columns of <c>target</c> the SQL definition uses; derived and stored when the definition is validated.</summary>
    public IReadOnlyList<string> SqlTargetColumns { get; init; } = [];

    /// <summary>Qualified key columns of the definition (<c>alias.column</c>); the protected object needs columns of the same name.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Optional validity window of each key (<c>alias.column</c>), compared with the binding's time column.</summary>
    public string? ValidFromColumn { get; init; }
    public string? ValidToColumn { get; init; }

    /// <summary>Filters this one replaces on objects where both apply.</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];

    public ManagedBy? ManagedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public FilterApprovalStatus Status { get; init; } = FilterApprovalStatus.Active;
    public Sid? CreatedBy { get; init; }
    public Sid? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>Hash stored with the row (set by the repository); differs from <see cref="ComputeDefinitionHash"/> after a write outside Autheris.</summary>
    public string? StoredDefinitionHash { get; init; }

    /// <summary>Column names the protected object must have (the unqualified key columns).</summary>
    public IReadOnlyList<string> TargetKeyColumns =>
        Sql != null ? SqlTargetColumns : KeyColumns.Select(k => VirtualFilterNames.ColumnOf(k)).ToList();

    /// <summary>Maximum length of a SQL definition.</summary>
    public const int MaxSqlLength = 4000;

    public void Validate()
    {
        VirtualFilterNames.ValidateFilterName(Name, nameof(Name));
        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new ArgumentException("A virtual filter needs a data source.", nameof(Source));
        }

        if ((Structured == null) == (Sql == null))
        {
            throw new ArgumentException("A virtual filter needs exactly one definition: structured or sql.", nameof(Structured));
        }

        if (Sql != null)
        {
            if (string.IsNullOrWhiteSpace(Sql) || Sql.Length > MaxSqlLength || Sql.Contains('\0'))
            {
                throw new ArgumentException($"The sql definition must be non-empty and at most {MaxSqlLength} characters.", nameof(Sql));
            }

            if (KeyColumns.Count > 0 || ValidFromColumn != null || ValidToColumn != null)
            {
                throw new ArgumentException("A sql definition states keys and time windows in the predicate (target.<column>), not in key_columns or valid_from/valid_to.", nameof(Sql));
            }

            ValidateSupersedesNames();
            return;
        }

        var aliases = ValidateDefinition(Structured!);

        if (KeyColumns.Count == 0)
        {
            throw new ArgumentException("A virtual filter needs at least one key column.", nameof(KeyColumns));
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KeyColumns)
        {
            VirtualFilterNames.ValidateQualifiedColumn(key, aliases, nameof(KeyColumns));
            if (!targets.Add(VirtualFilterNames.ColumnOf(key)))
            {
                throw new ArgumentException($"The key column name '{VirtualFilterNames.ColumnOf(key)}' appears more than once.", nameof(KeyColumns));
            }
        }

        if (ValidFromColumn != null) VirtualFilterNames.ValidateQualifiedColumn(ValidFromColumn, aliases, nameof(ValidFromColumn));
        if (ValidToColumn != null) VirtualFilterNames.ValidateQualifiedColumn(ValidToColumn, aliases, nameof(ValidToColumn));

        ValidateSupersedesNames();
    }

    private void ValidateSupersedesNames()
    {
        foreach (var superseded in Supersedes)
        {
            VirtualFilterNames.ValidateFilterName(superseded, nameof(Supersedes));
            if (string.Equals(superseded, Name, StringComparison.Ordinal))
            {
                throw new ArgumentException("A virtual filter cannot supersede itself.", nameof(Supersedes));
            }
        }
    }

    /// <summary>SHA-256 over the canonical definition (drift detection between the file repository and Autheris).</summary>
    public string ComputeDefinitionHash()
    {
        var sb = new StringBuilder();
        sb.Append("name=").Append(Name).Append('\n').Append("source=").Append(Source).Append('\n');
        if (Structured != null)
        {
            sb.Append("from=").Append(Structured.From.ToString()).Append(" as ").Append(Structured.FromAlias).Append('\n');
            foreach (var join in Structured.Joins)
            {
                sb.Append("join=").Append(join.Table.ToString()).Append(" as ").Append(join.Alias)
                  .Append(" on ").Append(join.LeftColumn).Append('=').Append(join.RightColumn).Append('\n');
            }

            foreach (var condition in Structured.Where)
            {
                sb.Append("where=").Append(condition.Column).Append(' ').Append(condition.Operator).Append(' ').Append(condition.Value ?? "\\0").Append('\n');
            }
        }

        if (Sql != null)
        {
            sb.Append("sql=").Append(Sql.ReplaceLineEndings("\n").Trim()).Append('\n');
        }

        sb.Append("keys=").AppendJoin(',', KeyColumns).Append('\n')
          .Append("valid_from=").Append(ValidFromColumn).Append('\n')
          .Append("valid_to=").Append(ValidToColumn).Append('\n')
          .Append("supersedes=").AppendJoin(',', Supersedes.OrderBy(s => s, StringComparer.Ordinal));
        return VirtualFilterNames.Sha256(sb.ToString());
    }

    private HashSet<string> ValidateDefinition(StructuredFilterDefinition definition)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateTable(definition.From, definition.FromAlias, aliases);
        foreach (var join in definition.Joins)
        {
            ValidateTable(join.Table, join.Alias, aliases);
            VirtualFilterNames.ValidateQualifiedColumn(join.LeftColumn, aliases, "Joins");
            VirtualFilterNames.ValidateQualifiedColumn(join.RightColumn, aliases, "Joins");
        }

        foreach (var condition in definition.Where)
        {
            VirtualFilterNames.ValidateQualifiedColumn(condition.Column, aliases, "Where");
            bool needsValue = condition.Operator is FilterConditionOperator.Eq or FilterConditionOperator.NotEq;
            if (needsValue != (condition.Value != null))
            {
                throw new ArgumentException($"The condition on '{condition.Column}' {(needsValue ? "needs" : "takes no")} value.", nameof(definition));
            }
        }

        return aliases;
    }

    private void ValidateTable(TableIdentifier table, string alias, HashSet<string> aliases)
    {
        if (!string.Equals(table.Domain, Source, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The table '{table.ToString()}' is not in the filter's data source '{Source}'.", nameof(table));
        }

        VirtualFilterNames.ValidateIdentifier(table.Schema, nameof(Structured));
        VirtualFilterNames.ValidateIdentifier(table.TableName, nameof(Structured));
        VirtualFilterNames.ValidateIdentifier(alias, nameof(Structured));
        if (!aliases.Add(alias))
        {
            throw new ArgumentException($"The alias '{alias}' is used more than once.", nameof(alias));
        }
    }
}

/// <summary>
/// One filter of an <see cref="AccessProfile"/>: applies the virtual filter to the objects matched by
/// <see cref="TargetPattern"/> (or the profile's scope). A binding only restricts: it is combined with AND with the
/// consent decision and never grants access.
/// </summary>
public sealed record FilterBinding
{
    public string FilterName { get; init; } = string.Empty;

    /// <summary>Pattern <c>source.schema.object[.column]</c>; null means the profile's scope.</summary>
    public string? TargetPattern { get; init; }

    public FilterObjectKinds ObjectKinds { get; init; } = FilterObjectKinds.Relation | FilterObjectKinds.ProcedureResult;

    /// <summary>Column of the protected object compared with the filter's validity window.</summary>
    public string? TimeColumn { get; init; }

    /// <summary>Key column name of the filter → column name of the protected object (default: same name).</summary>
    public IReadOnlyDictionary<string, string>? ColumnMap { get; init; }

    public void Validate()
    {
        VirtualFilterNames.ValidateFilterName(FilterName, nameof(FilterName));
        if (TargetPattern != null && !ObjectPattern.TryParse(TargetPattern, out _, out var patternError))
        {
            throw new ArgumentException($"Invalid target pattern of '{FilterName}': {patternError}", nameof(TargetPattern));
        }

        if (ObjectKinds == FilterObjectKinds.None)
        {
            throw new ArgumentException($"The binding of '{FilterName}' needs at least one object kind.", nameof(ObjectKinds));
        }

        if (TimeColumn != null) VirtualFilterNames.ValidateIdentifier(TimeColumn, nameof(TimeColumn));
        foreach (var (key, target) in ColumnMap ?? new Dictionary<string, string>())
        {
            VirtualFilterNames.ValidateIdentifier(key, nameof(ColumnMap));
            VirtualFilterNames.ValidateIdentifier(target, nameof(ColumnMap));
        }
    }

    internal void AppendCanonical(StringBuilder sb) =>
        sb.Append("binding=").Append(FilterName)
          .Append(" target=").Append(TargetPattern)
          .Append(" kinds=").Append((int)ObjectKinds)
          .Append(" time=").Append(TimeColumn)
          .Append(" map=").AppendJoin(',', (ColumnMap ?? new Dictionary<string, string>())
              .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
          .Append('\n');
}

/// <summary>
/// The virtual filters of one grantee (design 3.1): grantee, scope, bindings and what applies to objects in the scope
/// that no binding covers (<see cref="Uncovered"/>, on the profile because filters of one grantee complement each other).
/// </summary>
public sealed record AccessProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TenantId TenantId { get; init; } = TenantId.LegacySingleTenant;
    public string Name { get; init; } = string.Empty;
    public GranteeType GranteeType { get; init; } = GranteeType.User;
    public Sid? GranteeSid { get; init; }
    public string? RoleName { get; init; }

    /// <summary>Pattern <c>source.schema.object</c> (three segments) the profile governs.</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>Mandatory: objects in the scope no binding covers are denied or left to the consents (no silent default).</summary>
    public UncoveredPolicy? Uncovered { get; init; }

    public IReadOnlyList<FilterBinding> Bindings { get; init; } = [];

    public ManagedBy? ManagedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public FilterApprovalStatus Status { get; init; } = FilterApprovalStatus.Active;
    public Sid? CreatedBy { get; init; }
    public Sid? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>Hash stored with the row (set by the repository).</summary>
    public string? StoredDefinitionHash { get; init; }

    public void Validate()
    {
        VirtualFilterNames.ValidateFilterName(Name, nameof(Name));
        switch (GranteeType)
        {
            case GranteeType.Role when string.IsNullOrWhiteSpace(RoleName):
                throw new ArgumentException("A role profile needs a role name.", nameof(RoleName));
            case GranteeType.User or GranteeType.Group or GranteeType.ServicePrincipal when GranteeSid == null:
                throw new ArgumentException("A user, group or service principal profile needs a SID.", nameof(GranteeSid));
        }

        if (!ObjectPattern.TryParse(Scope, out var scope, out var scopeError))
        {
            throw new ArgumentException($"Invalid scope: {scopeError}", nameof(Scope));
        }

        if (scope!.HasColumnSegment)
        {
            throw new ArgumentException("The scope addresses objects (source.schema.object), not columns.", nameof(Scope));
        }

        if (Uncovered == null)
        {
            throw new ArgumentException("A profile must state uncovered (deny or skip).", nameof(Uncovered));
        }

        if (Bindings.Count == 0)
        {
            throw new ArgumentException("A profile needs at least one binding.", nameof(Bindings));
        }

        foreach (var binding in Bindings)
        {
            binding.Validate();
        }

        var duplicate = Bindings.GroupBy(b => (b.FilterName, b.TargetPattern)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"The filter '{duplicate.Key.FilterName}' is bound twice to the same target.", nameof(Bindings));
        }
    }

    public string ComputeDefinitionHash()
    {
        var sb = new StringBuilder()
            .Append("name=").Append(Name).Append('\n')
            .Append("grantee=").Append(GranteeType).Append(':').Append(GranteeSid?.Value).Append(':').Append(RoleName).Append('\n')
            .Append("scope=").Append(Scope).Append('\n')
            .Append("uncovered=").Append(Uncovered).Append('\n');
        foreach (var binding in Bindings.OrderBy(b => b.FilterName, StringComparer.Ordinal).ThenBy(b => b.TargetPattern, StringComparer.Ordinal))
        {
            binding.AppendCanonical(sb);
        }

        return VirtualFilterNames.Sha256(sb.ToString());
    }
}

/// <summary>Name rules shared by filters and profiles.</summary>
public static partial class VirtualFilterNames
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FilterNameRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentifierRegex();

    public static void ValidateFilterName(string? name, string paramName)
    {
        if (name == null || !FilterNameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid filter name (lower case letters, digits and '_', starting with a letter, at most 64 characters).", paramName);
        }
    }

    public static void ValidateIdentifier(string? name, string paramName)
    {
        if (name == null || !IdentifierRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier.", paramName);
        }
    }

    /// <summary><c>alias.column</c> with a known alias and simple names.</summary>
    public static void ValidateQualifiedColumn(string? qualified, IReadOnlySet<string> aliases, string paramName)
    {
        var parts = qualified?.Split('.') ?? [];
        if (parts.Length != 2)
        {
            throw new ArgumentException($"'{qualified}' must be qualified as alias.column.", paramName);
        }

        ValidateIdentifier(parts[0], paramName);
        ValidateIdentifier(parts[1], paramName);
        if (!aliases.Contains(parts[0]))
        {
            throw new ArgumentException($"'{qualified}' refers to an unknown alias '{parts[0]}'.", paramName);
        }
    }

    public static string ColumnOf(string qualified) => qualified[(qualified.IndexOf('.') + 1)..];

    internal static string Sha256(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
