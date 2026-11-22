using Autheris.Application.Governance.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Protection;

[Trait("Category", "Security")]
public sealed class EffectiveProtectionResolverTests
{
    private static readonly ContainerRef HrDomainRef = new(ContainerKind.Domain, Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Sid HrOwnerGroup = new("S-1-5-21-1001-2002-3003-512");

    [Fact]
    public void Unknown_class_string_resolves_confidential()
    {
        var parsedClass = ProtectionClassExtensions.ParseOrConfidential("UNKNOWN_CLASS_TIER");
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            parsedClass,
            CatalogVisibilityMode.Open);

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table: new ObjectProtection((ProtectionClass)99),
            columnOverrides: new Dictionary<string, ObjectProtection>
            {
                ["badge_code"] = new((ProtectionClass)(-42))
            },
            columnNames: ["employee_id", "badge_code"]);

        parsedClass.ShouldBe(ProtectionClass.Confidential);
        resolved.DeclaredTableClass.ShouldBe(ProtectionClass.Confidential);
        resolved.TableClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["employee_id"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["badge_code"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);

        var nullDefaultContainer = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            DefaultClass: null,
            CatalogVisibilityMode.Open);
        var nullDefaultResolved = EffectiveProtectionResolver.Resolve(
            nullDefaultContainer,
            table: null,
            columnOverrides: null,
            columnNames: ["employee_id"]);
        nullDefaultResolved.DeclaredTableClass.ShouldBe(ProtectionClass.Confidential);
        nullDefaultResolved.TableClass.ShouldBe(ProtectionClass.Confidential);
    }

    [Fact]
    public void Missing_domain_row_resolves_unassigned()
    {
        var fromNull = EffectiveProtectionResolver.Resolve(
            container: null,
            table: null,
            columnOverrides: null,
            columnNames: ["employee_id", "email"]);

        var fromInvalid = EffectiveProtectionResolver.Resolve(
            container: ContainerDefaults.Unassigned,
            table: null,
            columnOverrides: null,
            columnNames: ["employee_id", "email"]);

        foreach (var resolved in new[] { fromNull, fromInvalid })
        {
            resolved.Container.ShouldBe(new ContainerRef(ContainerKind.Domain, Guid.Empty));
            resolved.OwnerGroupSid.ShouldBeNull();
            resolved.TableClass.ShouldBe(ProtectionClass.Confidential);
            resolved.DeclaredTableClass.ShouldBe(ProtectionClass.Confidential);
            resolved.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
            resolved.Columns["employee_id"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
            resolved.Columns["email"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
            resolved.Columns["email"].EffectiveMaskCategory.ShouldBeNull();
        }
    }

    [Fact]
    public void Column_looser_clamped()
    {
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            ProtectionClass.Internal,
            CatalogVisibilityMode.OnRequest);
        var table = new ObjectProtection(ProtectionClass.Confidential);
        var overrides = new Dictionary<string, ObjectProtection>(StringComparer.Ordinal)
        {
            ["public_hint"] = new(ProtectionClass.Public),
            ["internal_hint"] = new(ProtectionClass.Internal)
        };

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table,
            overrides,
            ["employee_id", "public_hint", "internal_hint"]);

        resolved.DeclaredTableClass.ShouldBe(ProtectionClass.Confidential);
        resolved.TableClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["employee_id"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["public_hint"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["internal_hint"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
    }

    [Fact]
    public void Table_class_rolls_up_to_strictest_column()
    {
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            ProtectionClass.Public,
            CatalogVisibilityMode.Open);
        var table = new ObjectProtection(ProtectionClass.Internal);
        var overrides = new Dictionary<string, ObjectProtection>
        {
            ["email"] = new(ProtectionClass.Confidential),
            ["ssn"] = new(ProtectionClass.Secret)
        };

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table,
            overrides,
            ["id", "email", "ssn"]);

        resolved.DeclaredTableClass.ShouldBe(ProtectionClass.Internal);
        resolved.TableClass.ShouldBe(ProtectionClass.Secret);
        resolved.Columns["id"].EffectiveClass.ShouldBe(ProtectionClass.Internal);
        resolved.Columns["email"].EffectiveClass.ShouldBe(ProtectionClass.Confidential);
        resolved.Columns["ssn"].EffectiveClass.ShouldBe(ProtectionClass.Secret);
    }

    [Fact]
    public void Internal_table_with_secret_column_is_secret()
    {
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            ProtectionClass.Internal,
            CatalogVisibilityMode.Open);
        var table = new ObjectProtection(ProtectionClass.Internal);
        var overrides = new Dictionary<string, ObjectProtection>
        {
            ["tax_id"] = new(ProtectionClass.Secret)
        };

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table,
            overrides,
            columnNames: ["full_name"]);

        resolved.DeclaredTableClass.ShouldBe(ProtectionClass.Internal);
        resolved.TableClass.ShouldBe(ProtectionClass.Secret);
        resolved.Columns["full_name"].EffectiveClass.ShouldBe(ProtectionClass.Internal);
        resolved.Columns["tax_id"].EffectiveClass.ShouldBe(ProtectionClass.Secret);
    }

    [Theory]
    [InlineData("hr.employees", ProtectionClass.Confidential, null, ProtectionClass.Confidential, ProtectionClass.Confidential)]
    [InlineData("hr.directory", ProtectionClass.Confidential, ProtectionClass.Public, ProtectionClass.Public, ProtectionClass.Public)]
    [InlineData("hr.payroll", ProtectionClass.Internal, ProtectionClass.Internal, ProtectionClass.Internal, ProtectionClass.Secret)]
    public void Human_resources_tables_inherit_and_override_protection_classes(
        string tableName,
        ProtectionClass domainDefault,
        ProtectionClass? tableOverride,
        ProtectionClass expectedDeclared,
        ProtectionClass expectedRolledUp)
    {
        _ = tableName;
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            domainDefault,
            CatalogVisibilityMode.OnRequest);
        var table = tableOverride is { } tc ? new ObjectProtection(tc) : null;
        IReadOnlyDictionary<string, ObjectProtection>? overrides = expectedRolledUp == ProtectionClass.Secret
            ? new Dictionary<string, ObjectProtection> { ["iban"] = new(ProtectionClass.Secret, "BANK_IBAN") }
            : null;

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table,
            overrides,
            ["employee_id", "department"]);

        resolved.Container.ShouldBe(HrDomainRef);
        resolved.OwnerGroupSid.ShouldBe(HrOwnerGroup);
        resolved.DeclaredTableClass.ShouldBe(expectedDeclared);
        resolved.TableClass.ShouldBe(expectedRolledUp);
        resolved.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);
    }

    [Fact]
    public void MaskCategory_inherits_from_table_and_allows_column_override_case_insensitively()
    {
        var container = new ContainerDefaults(
            HrDomainRef,
            HrOwnerGroup,
            ProtectionClass.Confidential,
            CatalogVisibilityMode.Open);
        var table = new ObjectProtection(ProtectionClass.Confidential, "  PII_HR  ");
        var caseSensitiveOverrides = new Dictionary<string, ObjectProtection>(StringComparer.Ordinal)
        {
            ["EMAIL"] = new(null, "  "),
            ["Salary"] = new(ProtectionClass.Secret, "  COMPENSATION  ")
        };

        var resolved = EffectiveProtectionResolver.Resolve(
            container,
            table,
            caseSensitiveOverrides,
            ["EmployeeId", "email", "  ", "salary"]);

        resolved.TableMaskCategory.ShouldBe("PII_HR");
        resolved.Columns.Count.ShouldBe(3);
        resolved.Columns["employeeid"].EffectiveMaskCategory.ShouldBe("PII_HR");
        resolved.Columns["EMAIL"].EffectiveMaskCategory.ShouldBe("PII_HR");
        resolved.Columns["SALARY"].EffectiveMaskCategory.ShouldBe("COMPENSATION");
        resolved.Columns["salary"].EffectiveClass.ShouldBe(ProtectionClass.Secret);
    }
}
