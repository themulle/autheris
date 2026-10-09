using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class ConsentResolutionTests
{
    private readonly TableIdentifier _testTable = new("finance", "dbo", "invoices");
    private readonly Sid _userSid = new("S-1-5-21-1001");
    private readonly Sid _groupFinance = new("S-1-5-21-2001");
    private readonly string _roleAnalyst = "FinanceAnalyst";
    private readonly ConsentResolutionService _service = new();

    private Consent CreateBaseConsent(
        ConsentEffect effect,
        GranteeType granteeType,
        Sid? granteeSid = null,
        string? roleName = null)
    {
        return new Consent
        {
            TableIdentifier = _testTable,
            Effect = effect,
            GranteeType = granteeType,
            GranteeSid = granteeSid,
            RoleName = roleName,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false
        };
    }

    [Fact]
    public void ResolveAccess_WhenNoConsents_ReturnsZeroTrustDeny()
    {
        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string> { _roleAnalyst },
            _testTable,
            Array.Empty<Consent>()
        );

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Zero Trust", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveAccess_WhenTableHardDenyExists_DeniesAccessRegardlessOfAllows()
    {
        var allowConsent = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);
        var denyConsent = CreateBaseConsent(ConsentEffect.Deny, GranteeType.Group, _groupFinance);

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { allowConsent, denyConsent }
        );

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Hard DENY", StringComparison.OrdinalIgnoreCase) || r.Contains("Table DENY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveAccess_SingleUserAllowWithoutColumnRules_GrantsClearOnQuery()
    {
        var allowConsent = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { allowConsent }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.DeniedReasons.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveAccess_GroupHasMask_UserHasAllowWithoutColumnRule_YieldsClear()
    {
        // Rule: Group has salary = MASK, User has direct ALLOW without column rule -> salary is CLEAR (maximum over A)
        var groupAllow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        var userAllow = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { groupAllow, userAllow }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void ResolveAccess_GroupHasMask_UserHasAllow_RoleHasHardDenyOnColumn_YieldsDeny()
    {
        // Rule: Group has salary = MASK, User has direct ALLOW, Role has hard DENY on salary -> salary is DENY
        var groupAllow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        var userAllow = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);

        var roleDeny = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.Role,
            RoleName = _roleAnalyst,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Deny }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string> { _roleAnalyst },
            _testTable,
            new[] { groupAllow, userAllow, roleDeny }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void ResolveAccess_MultipleAllowsWithRowFilters_CombinesWithOr()
    {
        // Zwei ALLOW-Consents mit Company = DE01 bzw. Company = AT01 -> Vereinigung der Zeilen
        var allowDe = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "Company",
                    Operator = "EQ",
                    ValueJson = "\"DE01\""
                }
            }
        };

        var allowAt = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "Company",
                    Operator = "EQ",
                    ValueJson = "\"AT01\""
                }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { allowDe, allowAt }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("DE01");
        decision.CombinedRowFilterSql.ShouldContain("AT01");
        decision.CombinedRowFilterSql.ShouldContain("OR");
    }

    [Fact]
    public void ResolveAccess_OneAllowHasNoRowFilter_AllowsAllRows()
    {
        var filteredAllow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter { FilterGroup = 1, ColumnName = "Company", Operator = "EQ", ValueJson = "\"DE01\"" }
            }
        };

        var unconstrainedAllow = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { filteredAllow, unconstrainedAllow }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBeNull(); // Unconstrained access means no WHERE clause filter
    }

    [Fact]
    public void ResolveAccess_DenyRowFilter_AppendedWithAndNot()
    {
        var allowAll = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);
        var denyBlocked = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter { FilterGroup = 1, ColumnName = "Status", Operator = "EQ", ValueJson = "\"BLOCKED\"" }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { allowAll, denyBlocked }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("NOT");
        decision.CombinedRowFilterSql.ShouldContain("BLOCKED");
    }

    [Fact]
    public void ResolveAccess_IgnoresExpiredOrRevokedConsents()
    {
        var expiredAllow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
            ValidTo = DateTimeOffset.UtcNow.AddDays(-1),
            IsRevoked = false
        };

        var revokedAllow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = true,
            RevokedAt = DateTimeOffset.UtcNow
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { expiredAllow, revokedAllow }
        );

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Zero Trust", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveAccess_RestrictedColumnAllow_DoesNotGrantClearOnOtherColumns()
    {
        // Consent 1 grants MASK only on salary
        var allowConsent = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        // Consent 2 (DENY) hard-denies secret_note
        var denyConsent = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "secret_note", AccessLevel = ColumnAccessLevel.Deny }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { allowConsent, denyConsent }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Mask);
        // secret_note was never granted in any ALLOW consent, so it must NOT be CLEAR!
        decision.ColumnAccess["secret_note"].ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void ResolveAccess_DisjointRestrictedAllows_RetainRespectiveLevelsWithoutPromotingToClear()
    {
        // Consent 1: salary = MASK
        var allow1 = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "salary", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        // Consent 2: bonus = MASK (different column)
        var allow2 = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = _groupFinance,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new[]
            {
                new ConsentColumnRule { ColumnName = "bonus", AccessLevel = ColumnAccessLevel.Mask }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid> { _groupFinance },
            new HashSet<string>(),
            _testTable,
            new[] { allow1, allow2 }
        );

        decision.IsAllowed.ShouldBeTrue();
        // Neither salary nor bonus should be promoted to CLEAR! Both must remain MASK!
        decision.ColumnAccess["salary"].ShouldBe(ColumnAccessLevel.Mask);
        decision.ColumnAccess["bonus"].ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void ResolveAccess_RowFilterWithInOperator_UsesSingleQuotesForStrings()
    {
        var allow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "CountryCode",
                    Operator = "IN",
                    ValueJson = "[\"DE\", \"CH\", \"AT\"]"
                }
            }
        };

        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { allow }
        );

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        // SQL string literals must use single quotes, NOT double quotes
        decision.CombinedRowFilterSql.ShouldContain("IN ('DE', 'CH', 'AT')");
        decision.CombinedRowFilterSql.ShouldContain("CountryCode");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"single_string\"")]
    [InlineData("{\"key\": \"val\"}")]
    public void ResolveAccess_WhenInOperatorHasNonArrayValue_ThrowsInvalidOperationException(string nonArrayJson)
    {
        var allow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "status",
                    Operator = "IN",
                    ValueJson = nonArrayJson
                }
            }
        };

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            _service.ResolveAccess(_userSid, new HashSet<Sid>(), new HashSet<string>(), _testTable, new[] { allow });
        });

        ex.Message.ShouldContain("Array");
    }

    [Fact]
    public void RR_L4_01_DenyRowFilter_With_UserAttribute_FailsClosed_Under_Not()
    {
        // Arrange: Allow all rows, but Deny rows where CostCenter = @user.CostCenter (USER_ATTRIBUTE)
        var allowAll = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, _userSid);
        var denyUserAttr = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "CostCenter",
                    Operator = "EQ",
                    ValueSource = "USER_ATTRIBUTE",
                    ValueJson = "\"user.CostCenter\""
                }
            }
        };

        // Act
        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { allowAll, denyUserAttr }
        );

        // Assert: Must NOT generate NOT (1 = 0) which would be TRUE (allow-all).
        // It must generate NOT (1 = 1) which is FALSE, blocking rows!
        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldNotContain("NOT (1 = 0)");
        decision.CombinedRowFilterSql.ShouldContain("NOT (1 = 1)");
    }

    [Fact]
    public void RR_L5_01_PostgreSql_Backslash_In_Literals_Is_Not_Doubled()
    {
        // Arrange: AD Domain user in row filter value: ACME\Sales
        var allow = new Consent
        {
            TableIdentifier = _testTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = _userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new[]
            {
                new ConsentRowFilter
                {
                    FilterGroup = 1,
                    ColumnName = "Department",
                    Operator = "EQ",
                    ValueJson = "\"ACME\\\\Sales\"" // JSON string representing ACME\Sales
                }
            }
        };

        // Act
        var decision = _service.ResolveAccess(
            _userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            _testTable,
            new[] { allow },
            dialect: DatabaseDialect.PostgreSql
        );

        // Assert: In PostgreSQL with standard_conforming_strings=on, ACME\Sales is formatted as 'ACME\Sales', NOT 'ACME\\Sales'
        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldContain("'ACME\\Sales'");
        decision.CombinedRowFilterSql.ShouldNotContain("'ACME\\\\Sales'");
    }

    [Fact]
    public void ResolveAccess_WhenConsentBoundToOnPremSid_AndCallerHasOidPrimary_WithOnPremSidInAllUserSids_ShouldAllowAccess()
    {
        var onpremSid = new Sid("S-1-5-21-ONPREM-DAVID");
        var entraOid = new Sid("c032cb03-518d-4e92-9430-803513a96860");

        var allowConsent = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, onpremSid);

        var decision = _service.ResolveAccess(
            userSid: entraOid,
            subjectGroupSids: new HashSet<Sid>(),
            userRoles: new HashSet<string>(),
            table: _testTable,
            activeConsents: new[] { allowConsent },
            allUserSids: new HashSet<Sid> { entraOid, onpremSid }
        );

        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ResolveAccess_WhenConsentBoundToOnPremSid_AndCallerHasOidPrimary_WithoutOnPremSid_ShouldDenyAccess()
    {
        var onpremSid = new Sid("S-1-5-21-ONPREM-DAVID");
        var entraOid = new Sid("c032cb03-518d-4e92-9430-803513a96860");

        var allowConsent = CreateBaseConsent(ConsentEffect.Allow, GranteeType.User, onpremSid);

        var decision = _service.ResolveAccess(
            userSid: entraOid,
            subjectGroupSids: new HashSet<Sid>(),
            userRoles: new HashSet<string>(),
            table: _testTable,
            activeConsents: new[] { allowConsent },
            allUserSids: new HashSet<Sid> { entraOid }
        );

        decision.IsAllowed.ShouldBeFalse();
    }
}

