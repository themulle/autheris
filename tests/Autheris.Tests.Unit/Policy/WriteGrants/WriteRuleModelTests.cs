using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy.WriteGrants;

[Trait("Category", "Security")]
public sealed class WriteRuleModelTests
{
    [Fact]
    public void Insert_RequiresColumns()
    {
        var emptyColumns = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, [], WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => emptyColumns.Validate());

        var nullColumns = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, null!, WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => nullColumns.Validate());
    }

    [Fact]
    public void Update_RequiresColumns()
    {
        var emptyColumns = new WriteRule(Guid.NewGuid(), WriteOperation.Update, [], WriteRowScope.AllRows);
        Should.Throw<InvalidOperationException>(() => emptyColumns.Validate());

        var nullColumns = new WriteRule(Guid.NewGuid(), WriteOperation.Update, null!, WriteRowScope.AllRows);
        Should.Throw<InvalidOperationException>(() => nullColumns.Validate());
    }

    [Fact]
    public void Delete_RejectsColumns()
    {
        var withColumns = new WriteRule(Guid.NewGuid(), WriteOperation.Delete, ["id"], WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => withColumns.Validate());
    }

    [Fact]
    public void Delete_WithoutColumns_ValidatesSuccessfully()
    {
        var deleteRule = new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.OwnRows);
        Should.NotThrow(() => deleteRule.Validate());
    }

    [Fact]
    public void Columns_MustBeDistinct_CaseInsensitive()
    {
        var duplicateRule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["colA", "COLA"], WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => duplicateRule.Validate());
    }

    [Fact]
    public void Columns_CannotContainWhitespace()
    {
        var whitespaceRule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["valid", "   "], WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => whitespaceRule.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void MaxRowsPerStatement_ZeroOrNegative_Throws(long maxRows)
    {
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["col1"], WriteRowScope.OwnRows, MaxRowsPerStatement: maxRows);
        Should.Throw<InvalidOperationException>(() => rule.Validate());
    }

    [Fact]
    public void MaxRowsPerStatement_PositiveOrNull_Validates()
    {
        var withPositive = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["col1"], WriteRowScope.OwnRows, MaxRowsPerStatement: 500);
        Should.NotThrow(() => withPositive.Validate());

        var withNull = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["col1"], WriteRowScope.OwnRows, MaxRowsPerStatement: null);
        Should.NotThrow(() => withNull.Validate());
    }

    [Fact]
    public void WriteRule_WithRowFilter_ValidatesSuccessfully()
    {
        using var doc = JsonDocument.Parse("{\"dept\": \"finance\"}");
        var entry = new GrantRowScopeEntry("DeptPolicy", new Dictionary<string, JsonElement> { ["dept"] = doc.RootElement.GetProperty("dept") });
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["salary"], WriteRowScope.OwnRows, RowFilter: [entry]);
        Should.NotThrow(() => rule.Validate());
    }

    [Fact]
    public void RoleGrantee_WithWriteRules_IsRejected()
    {
        var consent = new Consent
        {
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("corp", "sales", "orders"),
            TenantId = "tenant-1",
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role,
            RoleId = Guid.NewGuid(),
            RoleName = "SalesManager",
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(2),
            WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["name"], WriteRowScope.OwnRows)]
        };

        Should.Throw<InvalidOperationException>(() => consent.Validate());
    }

    [Fact]
    public void DenyConsent_WithWriteRules_IsRejected()
    {
        var consent = new Consent
        {
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("corp", "sales", "orders"),
            TenantId = "tenant-1",
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-user-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(2),
            WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["name"], WriteRowScope.OwnRows)]
        };

        Should.Throw<InvalidOperationException>(() => consent.Validate());
    }

    [Fact]
    public void UserGrantee_WithWriteRules_ValidatesSuccessfully()
    {
        var consent = new Consent
        {
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("corp", "sales", "orders"),
            TenantId = "tenant-1",
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-user-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(2),
            WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount", "status"], WriteRowScope.OwnRows)]
        };

        Should.NotThrow(() => consent.Validate());
    }

    [Fact]
    public void AccessProfileWildcard_WithWriteRules_IsRejected()
    {
        var profile = new AccessProfile
        {
            ProfileId = "prof-1",
            Name = "Wildcard Profile",
            TenantId = "tenant-1",
            TargetTables = ["corp.*"],
            WriteRules = [new AccessProfileWriteRule(Guid.NewGuid(), new TableIdentifier("corp", "sales", "orders"), WriteOperation.Insert, ["name"], WriteRowScope.OwnRows)]
        };

        Should.Throw<InvalidOperationException>(() => profile.Validate());
    }

    [Fact]
    public void AccessProfileWriteRule_WildcardTableIdentifier_IsRejected()
    {
        var writeRule = new AccessProfileWriteRule(Guid.NewGuid(), new TableIdentifier("corp", "sales", "*"), WriteOperation.Insert, ["name"], WriteRowScope.OwnRows);
        Should.Throw<InvalidOperationException>(() => writeRule.Validate());
    }

    [Fact]
    public void AccessProfile_WithWriteRules_AndExactTargetTables_ValidatesSuccessfully()
    {
        var profile = new AccessProfile
        {
            ProfileId = "prof-1",
            Name = "Exact Profile",
            TenantId = "tenant-1",
            TargetTables = ["corp.sales.orders"],
            WriteRules = [new AccessProfileWriteRule(Guid.NewGuid(), new TableIdentifier("corp", "sales", "orders"), WriteOperation.Insert, ["name"], WriteRowScope.OwnRows)]
        };

        Should.NotThrow(() => profile.Validate());
    }

    [Fact]
    public void TableColumn_WriteProtected_DefaultsFalse_AndCanBeSet()
    {
        var colDefault = new TableColumn { TableId = Guid.NewGuid(), ColumnName = "description" };
        colDefault.WriteProtected.ShouldBeFalse();

        var colProtected = new TableColumn { TableId = Guid.NewGuid(), ColumnName = "audit_key", WriteProtected = true };
        colProtected.WriteProtected.ShouldBeTrue();
    }
}
