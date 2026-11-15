namespace Autheris.Domain.Model;

public sealed class ConsentColumnRule
{
    private readonly ColumnAccessLevel _accessLevel = ColumnAccessLevel.Deny;

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConsentId { get; init; }
    public Guid TableColumnId { get; init; }
    public string ColumnName { get; init; } = string.Empty;
    public ColumnAccessLevel AccessLevel
    {
        get => _accessLevel;
        init => _accessLevel = Enum.IsDefined(value) ? value : ColumnAccessLevel.Deny;
    }
    public IReadOnlyList<string> ApplicableScopes { get; init; } = Array.Empty<string>();
}

public enum RowFilterType
{
    SimpleColumnPredicate = 0,   // Standard: Column = Value / IN (...)
    CrossSourceSetFilter = 2     // Multi-Source: Two-Phase GraphQL / DataLoader Virtual Set
}

public sealed class ConsentRowFilter
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConsentId { get; init; }
    public int FilterGroup { get; init; }
    public Guid TableColumnId { get; init; }
    public string ColumnName { get; init; } = string.Empty;
    public string Operator { get; init; } = "EQ"; // EQ, NEQ, IN, LT, GT, LIKE
    public string ValueType { get; init; } = "string"; // string, int, decimal, date
    public string ValueJson { get; init; } = "[]";
    public string ValueSource { get; init; } = "LITERAL"; // LITERAL, USER_ATTRIBUTE
    public string? UserAttribute { get; init; }
    public RowFilterType FilterType { get; init; } = RowFilterType.SimpleColumnPredicate;
}

/// <summary>an in-place description change of a non-revoked consent (applied inside<c>ReplaceConsentsAsync</c>; never touches validity, rules or the epoch).</summary>
public sealed record ConsentDescriptionUpdate(Guid ConsentId, TenantId Tenant, string? Description);

public sealed class Consent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableId { get; init; }
    public TableIdentifier TableIdentifier { get; init; }
    public string? ObjectPattern { get; init; }
    public string? DataSource { get; init; }
    public ColumnSetMode ColumnSetMode { get; init; } = ColumnSetMode.Explicit;
    public ConsentWorkflowMode WorkflowMode { get; init; } = ConsentWorkflowMode.Internal;
    public int? ProfileItemNo { get; init; }
    public Guid? ConsentRequestId { get; init; }
    public ConsentEffect Effect { get; init; } = ConsentEffect.Allow;
    public TenantId TenantId { get; set; }
    public GranteeType GranteeType { get; init; } = GranteeType.User;
    public Sid? GranteeSid { get; init; }

    /// <summary>Review E-10: who created the consent (approver); used as audit actor only, not persisted.</summary>
    public Sid? CreatedBySid { get; init; }
    public Guid? RoleId { get; init; }
    public string? RoleName { get; init; }
    public DateTimeOffset ValidFrom { get; init; }
    public DateTimeOffset ValidTo { get; init; }
    public bool IsRevoked { get; init; }
    public Sid? RevokedBySid { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokeReason { get; init; }

    /// <summary>number of approved renewals (extensions) of this grant; persisted by the repositories.</summary>
    public int RenewalCount { get; init; }
    public ConsentGrantSource GrantSource { get; init; } = ConsentGrantSource.ConsentRequest;
    public long GrantRecordSeq { get; init; }
    public Guid? ProfileAssignmentId { get; init; }
    public DateTimeOffset? ExpiryNoticeSentAt { get; init; }

    /// <summary>
    /// owner-authored explanation of why this grant exists (persisted, normalised by<c>UntrustedText</c>). Settable because
    /// the repositories hydrate it after the row mapper. Untrusted text: it never influences a decision; output only through
    /// <c>ConsentDescriptionText.ForOutput</c>.
    /// </summary>
    public string? Description { get; set; }

    public IReadOnlyList<ConsentColumnRule> ColumnRules { get; set; } = Array.Empty<ConsentColumnRule>();
    public IReadOnlyList<ConsentRowFilter> RowFilters { get; set; } = Array.Empty<ConsentRowFilter>();
    public IReadOnlyList<WriteRule> WriteRules { get; set; } = [];
    public IReadOnlyList<GrantRowScopeEntry> RowScope { get; set; } = [];
    public IReadOnlyList<string> ApprovingOwners { get; set; } = [];

    /// <summary>True when this grant targets objects dynamically via <see cref="ObjectPattern"/> rather than a fixed table.</summary>
    public bool IsPatternGrant => !string.IsNullOrWhiteSpace(ObjectPattern);

    /// <summary>expiry after the read-time clamp of legacy individual grants (never later than<see cref="ValidTo"/>).</summary>
    public DateTimeOffset EffectiveValidTo => IndividualGrantLimits.EffectiveConsentValidTo(GranteeType, ValidFrom, ValidTo, Effect);

    public bool IsActive(DateTimeOffset atTime)
    {
        if (IsRevoked) return false;
        if (ValidFrom > atTime) return false;
        if (atTime >= EffectiveValidTo) return false;
        return true;
    }

    public void Validate()
    {
        if (ValidTo <= ValidFrom)
        {
            throw new InvalidOperationException($"Consent {Id}: ValidTo ({ValidTo}) must be greater than ValidFrom ({ValidFrom}).");
        }

        if (GranteeType == GranteeType.Role && Effect != ConsentEffect.Deny)
        {
            throw new InvalidOperationException($"Consent {Id}: Role grantees are not supported; grant to a group or a user instead.");
        }

        if (GranteeType == GranteeType.Role && RoleId == null && string.IsNullOrEmpty(RoleName))
        {
            throw new InvalidOperationException($"Consent {Id}: RoleId or RoleName must be specified when GranteeType is Role.");
        }

        if (GranteeType != GranteeType.Role && (GranteeSid == null || string.IsNullOrWhiteSpace(GranteeSid.Value.Value)))
        {
            throw new InvalidOperationException($"Consent {Id}: GranteeSid must be specified when GranteeType is User, Group or ServicePrincipal.");
        }

        if (Effect == ConsentEffect.Deny && WriteRules.Count > 0)
        {
            throw new InvalidOperationException($"Consent {Id}: Deny consents cannot hold write rules.");
        }

        foreach (var rule in WriteRules)
        {
            rule.Validate();
        }

        ConsentRowFilterShape.Validate(RowFilters);

        foreach (var entry in RowScope)
        {
            if (string.IsNullOrWhiteSpace(entry.PolicyName))
            {
                throw new InvalidOperationException($"Consent {Id}: row-scope entry policy name must not be empty.");
            }

            if (!Enum.IsDefined(entry.Uncovered))
            {
                throw new InvalidOperationException($"Consent {Id}: invalid row-scope uncovered policy.");
            }
        }

        if (!Enum.IsDefined(ColumnSetMode))
        {
            throw new InvalidOperationException($"Consent {Id}: invalid column set mode.");
        }

        if (!Enum.IsDefined(WorkflowMode))
        {
            throw new InvalidOperationException($"Consent {Id}: invalid workflow mode.");
        }

        if (IsPatternGrant)
        {
            if (TableId != Guid.Empty)
            {
                throw new InvalidOperationException($"Consent {Id}: pattern grant must not specify a concrete TableId.");
            }

            if (!ObjectPatternSpec.TryParse(ObjectPattern, out var spec, out var error))
            {
                throw new InvalidOperationException($"Consent {Id}: invalid object pattern ({error}).");
            }

            var effectiveSource = string.IsNullOrWhiteSpace(DataSource) ? spec!.DataSource : DataSource.Trim();
            if (!string.Equals(effectiveSource, spec!.DataSource, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Consent {Id}: DataSource '{effectiveSource}' must match the literal source segment '{spec.DataSource}' of ObjectPattern.");
            }
        }
    }
}
