namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;

/// <summary>Value type of a caller attribute (PLAN-GOV-129 C-1). <see cref="Uuid"/> attributes are bound as canonical lower-case strings.</summary>
public enum CallerAttributeType
{
    Text = 0,
    Whole = 1,
    Number = 2,
    Flag = 3,
    Uuid = 4
}

public enum CallerAttributeCardinality
{
    Scalar = 0,
    List = 1
}

/// <summary>Which provider reads the attribute and its provider-specific, string-valued configuration (part of the definition hash).</summary>
public sealed record CallerAttributeProviderBinding(string Kind, IReadOnlyDictionary<string, string> Config)
{
    public const string Claims = "claims";
    public const string AttributeTable = "attribute-table";
    public const string ServiceSet = "service-set";

    public bool Equals(CallerAttributeProviderBinding? other) =>
        other is not null && string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
        Config.Count == other.Config.Count &&
        Config.All(p => other.Config.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.Ordinal));

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Kind);
}

/// <summary>
/// One registered caller attribute (REQ-GOV-129). The definition decides what the attribute is and where it comes from; values never
/// live here. <see cref="ComputeDefinitionHash"/> covers everything that changes the meaning (provider configuration included), so a
/// cached value can never outlive a definition change (BC-129-08).
/// </summary>
public sealed record CallerAttributeDefinition(
    Guid Id,
    TenantId Tenant,
    string Name,
    CallerAttributeType ValueType,
    CallerAttributeCardinality Cardinality,
    int MaxValues,
    int TtlSeconds,
    CallerAttributeProviderBinding Provider,
    string? Description,
    FilterApprovalStatus Status)
{
    public const int DefaultMaxValues = 200;
    public const int DefaultTtlSeconds = 60;
    public const int MaxTtlSecondsCeiling = 300;

    /// <summary>Lower-case, digits and single underscores; never <c>__</c> (the slot parameter name uses it as separator).</summary>
    public static readonly Regex NameGrammar = new(@"\A[a-z](?:[a-z0-9]|_(?!_)){0,62}[a-z0-9]\z|\A[a-z]\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 64 && NameGrammar.IsMatch(name);

    /// <summary>SHA-256 (hex, lower case) over a canonical, length-prefixed form of every field except <see cref="Id"/> and <see cref="Status"/>.</summary>
    public string ComputeDefinitionHash()
    {
        var sb = new StringBuilder("cad/v1");
        Append(sb, Tenant.Value);
        Append(sb, Name);
        Append(sb, ((int)ValueType).ToString(CultureInfo.InvariantCulture));
        Append(sb, ((int)Cardinality).ToString(CultureInfo.InvariantCulture));
        Append(sb, MaxValues.ToString(CultureInfo.InvariantCulture));
        Append(sb, TtlSeconds.ToString(CultureInfo.InvariantCulture));
        Append(sb, Provider.Kind);
        foreach (var pair in Provider.Config.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(sb, pair.Key);
            Append(sb, pair.Value);
        }

        Append(sb, Description ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static void Append(StringBuilder sb, string part) => sb.Append('|').Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
}

/// <summary>A registered attribute with its approval state (four-eyes, last-known-good: the active definition stays while a draft waits).</summary>
public sealed record CallerAttributeRecord(
    CallerAttributeDefinition Definition,
    string CreatedBy,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    CallerAttributeDefinition? Draft,
    string? DraftCreatedBy,
    bool PendingDeletion,
    string? DeletionRequestedBy,
    DateTimeOffset UpdatedAt)
{
    public string DefinitionHash => Definition.ComputeDefinitionHash();

    public string? DraftHash => Draft?.ComputeDefinitionHash();
}

/// <summary>Static values of an application-only caller (V-129-9, Q-129-6): per tenant, application id and attribute.</summary>
public sealed record ServiceAttributeSet(
    Guid Id,
    TenantId Tenant,
    string ApplicationId,
    string AttributeName,
    IReadOnlyList<string> Values,
    FilterApprovalStatus Status)
{
    public string ComputeValuesHash()
    {
        var sb = new StringBuilder("cas/v1");
        CallerAttributeDefinition.Append(sb, Tenant.Value);
        CallerAttributeDefinition.Append(sb, ApplicationId);
        CallerAttributeDefinition.Append(sb, AttributeName);
        foreach (var value in Values)
        {
            CallerAttributeDefinition.Append(sb, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>A service set with its approval state.</summary>
public sealed record ServiceAttributeSetRecord(
    ServiceAttributeSet Set,
    string CreatedBy,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<string>? DraftValues,
    string? DraftCreatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>All registered attributes and service sets with the generation they belong to.</summary>
public sealed record CallerAttributeStoreSnapshot(long Generation, IReadOnlyList<CallerAttributeRecord> Attributes, IReadOnlyList<ServiceAttributeSetRecord> ServiceSets)
{
    public static readonly CallerAttributeStoreSnapshot Empty = new(0, [], []);
}
