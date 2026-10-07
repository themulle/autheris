using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class DomainAndModelEdgeCasesTests
{
    #region Sid Edge Cases

    [Theory]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("S-1-5-32-544")]
    [InlineData("admin@corp.contoso.local")]
    [InlineData("c29665be-d731-4be5-9f57-df43bcae5264")]
    [InlineData("CN=Max Mustermann,OU=IT,DC=corp,DC=internal")]
    public void Sid_UnusualAndStandardFormats_PreserveValueAndToString(string sidValue)
    {
        var sid = new Sid(sidValue);

        sid.Value.ShouldBe(sidValue);
        sid.ToString().ShouldBe(sidValue);
        string implicitStr = sid;
        implicitStr.ShouldBe(sidValue);
        Sid implicitSid = sidValue;
        implicitSid.ShouldBe(sid);
    }

    [Fact]
    public void Sid_CaseInsensitivity_AndHashCodes_WorkConsistently()
    {
        var sidUpper = new Sid("S-1-5-21-ABCDEF");
        var sidLower = new Sid("s-1-5-21-abcdef");
        var sidMixed = new Sid("S-1-5-21-AbCdEf");

        sidUpper.Equals(sidLower).ShouldBeTrue();
        sidUpper.Equals(sidMixed).ShouldBeTrue();
        (sidUpper == sidLower).ShouldBeTrue();
        (sidUpper != sidMixed).ShouldBeFalse();

        sidUpper.GetHashCode().ShouldBe(sidLower.GetHashCode());
        sidUpper.GetHashCode().ShouldBe(sidMixed.GetHashCode());

        var set = new HashSet<Sid> { sidUpper };
        set.Contains(sidLower).ShouldBeTrue();
        set.Contains(sidMixed).ShouldBeTrue();
    }

    [Fact]
    public void Sid_NullOrEmptyValue_HandlesSafely()
    {
        var emptySid = new Sid(string.Empty);
        emptySid.Value.ShouldBe(string.Empty);
        emptySid.ToString().ShouldBe(string.Empty);
        emptySid.GetHashCode().ShouldBe(StringComparer.OrdinalIgnoreCase.GetHashCode(string.Empty));

        var defaultSid = default(Sid);
        defaultSid.Value.ShouldBeNull();
        defaultSid.GetHashCode().ShouldBe(StringComparer.OrdinalIgnoreCase.GetHashCode(string.Empty));
        defaultSid.Equals(emptySid).ShouldBeFalse();
    }

    [Fact]
    public void GetUserSid_ClaimsPrincipalPrecedence_PrefersPrimarySidOverObjectSidOverNameIdentifier()
    {
        // 1. PrimarySid wins over objectSid and NameIdentifier
        var principal1 = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "SID-PRIMARY"),
            new Claim("objectSid", "SID-OBJECT"),
            new Claim(ClaimTypes.NameIdentifier, "SID-NAMEID")
        }));
        principal1.GetUserSid().ShouldBe(new Sid("SID-PRIMARY"));

        // 2. objectSid wins over NameIdentifier when PrimarySid is absent
        var principal2 = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "SID-OBJECT"),
            new Claim(ClaimTypes.NameIdentifier, "SID-NAMEID")
        }));
        principal2.GetUserSid().ShouldBe(new Sid("SID-OBJECT"));

        // 3. NameIdentifier is used when PrimarySid and objectSid are absent
        var principal3 = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "SID-NAMEID")
        }));
        principal3.GetUserSid().ShouldBe(new Sid("SID-NAMEID"));

        // 4. Returns null if principal is null
        ((ClaimsPrincipal?)null).GetUserSid().ShouldBeNull();

        // 5. Returns null if claims exist but values are whitespace or empty
        var principalWhitespace = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "   "),
            new Claim("objectSid", ""),
            new Claim(ClaimTypes.NameIdentifier, "\t")
        }));
        principalWhitespace.GetUserSid().ShouldBeNull();
    }

    #endregion

    #region TableIdentifier Edge Cases

    [Theory]
    [InlineData("domain.schema.table", "domain", "schema", "table")]
    [InlineData("  domain  .  schema  .  table  ", "domain", "schema", "table")]
    [InlineData("schema.table", "default", "schema", "table")]
    [InlineData("  schema  .  table  ", "default", "schema", "table")]
    [InlineData("finance.dbo.finance_invoices_2026", "finance", "dbo", "finance_invoices_2026")]
    public void TableIdentifier_ParseAndTryParse_ValidInputs_ReturnExpectedSegments(
        string input, string expectedDomain, string expectedSchema, string expectedTable)
    {
        TableIdentifier.TryParse(input, out var id).ShouldBeTrue();
        id.Domain.ShouldBe(expectedDomain);
        id.Schema.ShouldBe(expectedSchema);
        id.TableName.ShouldBe(expectedTable);

        var parsed = TableIdentifier.Parse(input);
        parsed.Domain.ShouldBe(expectedDomain);
        parsed.Schema.ShouldBe(expectedSchema);
        parsed.TableName.ShouldBe(expectedTable);

        parsed.ToString().ShouldBe($"{expectedDomain}.{expectedSchema}.{expectedTable}");
        parsed.ToQualifiedName().ShouldBe($"{expectedSchema}.{expectedTable}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("singletoken")]
    [InlineData("one.two.three.four")]
    [InlineData("domain..table")]
    [InlineData(".schema.table")]
    [InlineData("domain.schema.")]
    [InlineData(" . . ")]
    public void TableIdentifier_ParseAndTryParse_InvalidInputs_ReturnFalseOrThrowFormatException(string? input)
    {
        TableIdentifier.TryParse(input, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => TableIdentifier.Parse(input!));
    }

    [Fact]
    public void TableIdentifier_CaseInsensitiveEquality_MatchesAcrossSegments()
    {
        var id1 = new TableIdentifier("FINANCE", "DBO", "INVOICES");
        var id2 = new TableIdentifier("finance", "dbo", "invoices");
        var id3 = new TableIdentifier("Finance", "Dbo", "Invoices");

        id1.Equals(id2).ShouldBeTrue();
        id2.Equals(id3).ShouldBeTrue();
        (id1 == id2).ShouldBeTrue();
        (id1 != id3).ShouldBeFalse();

        id1.GetHashCode().ShouldBe(id2.GetHashCode());
        id2.GetHashCode().ShouldBe(id3.GetHashCode());
    }

    #endregion

    #region DatabaseDialect & Identifier Validation

    [Theory]
    [InlineData("col")]
    [InlineData("COLUMN_NAME")]
    [InlineData("_private_col")]
    [InlineData("col2026")]
    [InlineData("_123")]
    public void DatabaseDialect_ValidateIdentifier_ValidIdentifiers_PassValidation(string validId)
    {
        Should.NotThrow(() => DatabaseDialectExtensions.ValidateIdentifier(validId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1leadingdigit")]
    [InlineData("col name with spaces")]
    [InlineData("col-with-hyphen")]
    [InlineData("col; DROP TABLE")]
    [InlineData("col'quote")]
    [InlineData("col\"quote")]
    [InlineData("col`backtick")]
    public void DatabaseDialect_ValidateIdentifier_InvalidIdentifiers_ThrowsArgumentException(string? invalidId)
    {
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier(invalidId!));
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "amount", "[amount]")]
    [InlineData(DatabaseDialect.PostgreSql, "amount", "\"amount\"")]
    [InlineData(DatabaseDialect.Sqlite, "amount", "\"amount\"")]
    [InlineData(DatabaseDialect.Oracle, "amount", "\"amount\"")]
    [InlineData(DatabaseDialect.Databricks, "amount", "`amount`")]
    public void DatabaseDialect_QuoteIdentifier_QuotesAccordingToDialectSpecification(
        DatabaseDialect dialect, string id, string expectedQuoted)
    {
        dialect.QuoteIdentifier(id).ShouldBe(expectedQuoted);
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "dbo.invoices", "[dbo].[invoices]")]
    [InlineData(DatabaseDialect.PostgreSql, "dbo.invoices", "\"dbo\".\"invoices\"")]
    [InlineData(DatabaseDialect.Databricks, "dbo.invoices", "`dbo`.`invoices`")]
    [InlineData(DatabaseDialect.Oracle, "dbo.invoices", "\"dbo\".\"invoices\"")]
    [InlineData(DatabaseDialect.Sqlite, "dbo.invoices", "\"dbo\".\"invoices\"")]
    public void DatabaseDialect_QuoteQualifiedColumn_QuotesEachSegmentAccordingToDialect(
        DatabaseDialect dialect, string qualifiedCol, string expectedQuoted)
    {
        dialect.QuoteQualifiedColumn(qualifiedCol).ShouldBe(expectedQuoted);
    }

    [Theory]
    [InlineData("mssql", DatabaseDialect.SqlServer)]
    [InlineData("  sqlserver  ", DatabaseDialect.SqlServer)]
    [InlineData("SQL_SERVER", DatabaseDialect.SqlServer)]
    [InlineData("Microsoft SQL Server", DatabaseDialect.SqlServer)]
    [InlineData("sqlite", DatabaseDialect.Sqlite)]
    [InlineData("sqlite3", DatabaseDialect.Sqlite)]
    [InlineData("postgres", DatabaseDialect.PostgreSql)]
    [InlineData("postgresql", DatabaseDialect.PostgreSql)]
    [InlineData("pgsql", DatabaseDialect.PostgreSql)]
    [InlineData("npgsql", DatabaseDialect.PostgreSql)]
    [InlineData("databricks", DatabaseDialect.Databricks)]
    [InlineData("spark", DatabaseDialect.Databricks)]
    [InlineData("sparksql", DatabaseDialect.Databricks)]
    [InlineData("oracle", DatabaseDialect.Oracle)]
    [InlineData("oracledb", DatabaseDialect.Oracle)]
    [InlineData("odp", DatabaseDialect.Oracle)]
    public void DatabaseDialect_ParseDialect_RecognizesAllCommonAliases(string? input, DatabaseDialect expectedDialect)
    {
        DatabaseDialectExtensions.ParseDialect(input).ShouldBe(expectedDialect);
    }

    #endregion

    #region Consent & Delegation Validation & Temporal Boundaries

    [Fact]
    public void Consent_Validate_WhenValidToLessThanOrEqualToValidFrom_ThrowsInvalidOperationException()
    {
        var now = DateTimeOffset.UtcNow;

        var invalidEqual = new Consent
        {
            TableIdentifier = new TableIdentifier("finance", "dbo", "invoices"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER"),
            ValidFrom = now,
            ValidTo = now
        };
        var exEqual = Should.Throw<InvalidOperationException>(() => invalidEqual.Validate());
        exEqual.Message.ShouldContain("ValidTo");

        var invalidLess = new Consent
        {
            TableIdentifier = new TableIdentifier("finance", "dbo", "invoices"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER"),
            ValidFrom = now,
            ValidTo = now.AddDays(-1)
        };
        var exLess = Should.Throw<InvalidOperationException>(() => invalidLess.Validate());
        exLess.Message.ShouldContain("ValidTo");
    }

    [Fact]
    public void Consent_Validate_WhenRoleMissingRoleIdAndRoleName_ThrowsInvalidOperationException()
    {
        var consent = new Consent
        {
            TableIdentifier = new TableIdentifier("finance", "dbo", "invoices"),
            GranteeType = GranteeType.Role,
            RoleId = null,
            RoleName = null,
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };

        var ex = Should.Throw<InvalidOperationException>(() => consent.Validate());
        ex.Message.ShouldContain("RoleId or RoleName must be specified");
    }

    [Fact]
    public void Consent_Validate_WhenUserOrGroupMissingGranteeSid_ThrowsInvalidOperationException()
    {
        var consentUser = new Consent
        {
            TableIdentifier = new TableIdentifier("finance", "dbo", "invoices"),
            GranteeType = GranteeType.User,
            GranteeSid = null,
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        Should.Throw<InvalidOperationException>(() => consentUser.Validate());

        var consentEmptySid = new Consent
        {
            TableIdentifier = new TableIdentifier("finance", "dbo", "invoices"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("   "),
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        Should.Throw<InvalidOperationException>(() => consentEmptySid.Validate());
    }

    [Fact]
    public void Consent_IsActive_TemporalBoundaryChecks()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddHours(2);

        var consent = new Consent
        {
            ValidFrom = from,
            ValidTo = to,
            IsRevoked = false
        };

        // Exactly at ValidFrom -> Active
        consent.IsActive(from).ShouldBeTrue();

        // 1 tick before ValidFrom -> Inactive
        consent.IsActive(from.AddTicks(-1)).ShouldBeFalse();

        // 1 tick before ValidTo -> Active
        consent.IsActive(to.AddTicks(-1)).ShouldBeTrue();

        // Exactly at ValidTo -> Inactive (exclusive upper bound)
        consent.IsActive(to).ShouldBeFalse();

        // After ValidTo -> Inactive
        consent.IsActive(to.AddMinutes(1)).ShouldBeFalse();

        // If revoked, always inactive even within valid window
        var revokedConsent = new Consent
        {
            ValidFrom = from,
            ValidTo = to,
            IsRevoked = true
        };
        revokedConsent.IsActive(from.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void DataOwnerDelegation_IsActive_TemporalBoundaryChecks()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(7);

        var delegation = new DataOwnerDelegation
        {
            ValidFrom = from,
            ValidTo = to
        };

        // Before start
        delegation.IsActive(from.AddMinutes(-1)).ShouldBeFalse();

        // Exactly at start -> Active
        delegation.IsActive(from).ShouldBeTrue();

        // Mid-period -> Active
        delegation.IsActive(from.AddDays(3)).ShouldBeTrue();

        // 1 tick before end -> Active
        delegation.IsActive(to.AddTicks(-1)).ShouldBeTrue();

        // Exactly at end -> Inactive
        delegation.IsActive(to).ShouldBeFalse();

        // After end -> Inactive
        delegation.IsActive(to.AddSeconds(1)).ShouldBeFalse();
    }

    #endregion

    #region PolicyEpoch Boundaries

    [Fact]
    public void PolicyEpoch_LargeValues_AndTimestampUpdates()
    {
        var table = new TableIdentifier("sales", "dbo", "orders");
        var epoch = new PolicyEpoch
        {
            TableId = Guid.NewGuid(),
            TableIdentifier = table,
            Epoch = long.MaxValue - 10,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        epoch.Epoch.ShouldBe(long.MaxValue - 10);
        epoch.Epoch++;
        epoch.Epoch.ShouldBe(long.MaxValue - 9);
    }

    #endregion

    #region MaskingRule Tenant-Scoping (SEC H-13)

    [Fact]
    public void MaskingRule_CreateTenantScopedHmacRule_WhenUnscoped_AppendsTenant()
    {
        var rule = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "master_key" };
        var scoped = MaskingRule.CreateTenantScopedHmacRule(rule, "tenant_a");

        scoped.HmacKeyId.ShouldBe("master_key|tenant:tenant_a");
    }

    [Fact]
    public void MaskingRule_CreateTenantScopedHmacRule_WhenAlreadyScopedToSameTenant_IsIdempotent()
    {
        var rule = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "master_key|tenant:tenant_a" };
        var scoped = MaskingRule.CreateTenantScopedHmacRule(rule, "tenant_a");

        scoped.ShouldBeSameAs(rule);
    }

    [Fact]
    public void MaskingRule_CreateTenantScopedHmacRule_WhenScopedToForeignTenant_ThrowsInvalidOperationException()
    {
        // SEC H-13: Catalog rule scoped to Tenant A must never be re-used or accessible by Tenant B
        var ruleTenantA = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "master_key|tenant:tenant_a" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            MaskingRule.CreateTenantScopedHmacRule(ruleTenantA, "tenant_b"));

        ex.Message.ShouldContain("tenant_a");
        ex.Message.ShouldContain("tenant_b");
    }

    #endregion
}
