namespace Autheris.Domain.Model;

public sealed class ConsentColumnRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConsentId { get; init; }
    public Guid TableColumnId { get; init; }
    public string ColumnName { get; init; } = string.Empty;
    public ColumnAccessLevel AccessLevel { get; init; } = ColumnAccessLevel.Deny;
}

public enum RowFilterType
{
    SimpleColumnPredicate = 0,   // Standard: Column = Value / IN (...)
    SubqueryCorrelated = 1,      // Single-Source: Correlated EXISTS / Subquery
    CrossSourceSetFilter = 2     // Multi-Source: Two-Phase GraphQL / DataLoader Virtual Set
}

public sealed class SubqueryJoinHop
{
    public TableIdentifier Table { get; init; }
    public string TableAlias { get; init; } = string.Empty;
    public string LeftJoinColumn { get; init; } = string.Empty;
    public string RightJoinColumn { get; init; } = string.Empty;
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

    // Advanced RLS: Correlated Subquery & Cross-Source Set Properties
    public RowFilterType FilterType { get; init; } = RowFilterType.SimpleColumnPredicate;
    public TableIdentifier? DependentTable { get; init; }
    public string? DependentTableAlias { get; init; }
    public string? ForeignKeyColumn { get; init; }
    public string? PrimaryKeyColumn { get; init; }
    public string? SubqueryFilterPredicateJson { get; init; }
    public string? TargetTemporalColumn { get; init; }
    public string? DependentValidFromColumn { get; init; }
    public string? DependentValidToColumn { get; init; }
    public string? TargetTableAlias { get; init; }
    public IReadOnlyList<SubqueryJoinHop>? AdditionalHops { get; init; }
}

public sealed class Consent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableId { get; init; }
    public TableIdentifier TableIdentifier { get; init; }
    public Guid? ConsentRequestId { get; init; }
    public ConsentEffect Effect { get; init; } = ConsentEffect.Allow;
    public TenantId TenantId { get; set; } = TenantId.LegacySingleTenant;
    public GranteeType GranteeType { get; init; } = GranteeType.User;
    public Sid? GranteeSid { get; init; }
    public Guid? RoleId { get; init; }
    public string? RoleName { get; init; }
    public DateTimeOffset ValidFrom { get; init; }
    public DateTimeOffset ValidTo { get; init; }
    public bool IsRevoked { get; init; }
    public Sid? RevokedBySid { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokeReason { get; init; }

    public IReadOnlyList<ConsentColumnRule> ColumnRules { get; set; } = Array.Empty<ConsentColumnRule>();
    public IReadOnlyList<ConsentRowFilter> RowFilters { get; set; } = Array.Empty<ConsentRowFilter>();

    public bool IsActive(DateTimeOffset atTime)
    {
        if (IsRevoked) return false;
        if (ValidFrom > atTime) return false;
        if (atTime >= ValidTo) return false;
        return true;
    }

    public void Validate()
    {
        if (ValidTo <= ValidFrom)
        {
            throw new InvalidOperationException($"Consent {Id}: ValidTo ({ValidTo}) must be greater than ValidFrom ({ValidFrom}).");
        }

        if (GranteeType == GranteeType.Role)
        {
            if (RoleId == null && string.IsNullOrEmpty(RoleName))
            {
                throw new InvalidOperationException($"Consent {Id}: RoleId or RoleName must be specified when GranteeType is Role.");
            }
        }
        else
        {
            if (GranteeSid == null || string.IsNullOrWhiteSpace(GranteeSid.Value.Value))
            {
                throw new InvalidOperationException($"Consent {Id}: GranteeSid must be specified when GranteeType is User, Group or ServicePrincipal.");
            }
        }
    }
}
