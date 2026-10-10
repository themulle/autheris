namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Virtual filters, phase 1: validation of filters and bindings (no silent defaults).</summary>
public sealed class VirtualFilterModelTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");

    internal static VirtualFilter DavidFilter(string name = "nicht_ausgelieferte_krane") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "lwetem_prod",
        KeyColumns = ["client.client_id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("lwetem_prod", "conf", "client"),
            FromAlias = "client",
            Joins =
            [
                new FilterJoin(new TableIdentifier("lwetem_prod", "md", "crane"), "crane", "crane.serial_number", "client.crane_serial_number")
            ],
            Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
        }
    };

    internal static VirtualFilterAccessProfile DavidProfile(string name = "david", params string[] filters) => new()
    {
        TenantId = Tenant,
        Name = name,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-LWE-DAVID"),
        Scope = "lwetem_prod.*.*",
        Uncovered = UncoveredPolicy.Deny,
        Bindings = (filters.Length == 0 ? ["nicht_ausgelieferte_krane"] : filters)
            .Select(f => new FilterBinding { FilterName = f, TargetPattern = "lwetem_prod.*.*.client_id" }).ToList()
    };

    [Fact]
    public void DavidCase_IsValid()
    {
        Should.NotThrow(() => DavidFilter().Validate());
        Should.NotThrow(() => DavidProfile().Validate());
        DavidFilter().TargetKeyColumns.ShouldBe(["client_id"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nicht")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    public void FilterName_MustBeLowerSnakeCase(string name)
    {
        Should.Throw<ArgumentException>(() => DavidFilter(name).Validate());
    }

    [Fact]
    public void FilterName_IsLimitedTo64Characters()
    {
        Should.NotThrow(() => DavidFilter("a" + new string('b', 63)).Validate());
        Should.Throw<ArgumentException>(() => DavidFilter("a" + new string('b', 64)).Validate());
    }

    [Fact]
    public void KeyColumns_AreRequired_AndQualifiedWithAnAliasOfTheDefinition()
    {
        Should.Throw<ArgumentException>(() => (DavidFilter() with { KeyColumns = [] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidFilter() with { KeyColumns = ["client_id"] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidFilter() with { KeyColumns = ["other.client_id"] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidFilter() with { KeyColumns = ["client.client_id", "crane.client_id"] }).Validate());
    }

    [Fact]
    public void ValidityColumns_MustReferenceAnAliasOfTheDefinition()
    {
        Should.NotThrow(() => (DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }).Validate());
        Should.Throw<ArgumentException>(() => (DavidFilter() with { ValidToColumn = "x.date_of_delivery" }).Validate());
    }

    [Fact]
    public void Identifiers_InTheDefinition_MustBeSimpleNames()
    {
        Should.Throw<ArgumentException>(() => (DavidFilter() with { KeyColumns = ["client.client_id; drop"] }).Validate());
        var badCondition = DavidFilter() with
        {
            Structured = DavidFilter().Structured! with { Where = [new FilterCondition("crane.is_delivered or 1=1", FilterConditionOperator.IsNull)] }
        };
        Should.Throw<ArgumentException>(() => badCondition.Validate());
    }

    [Fact]
    public void Definition_TablesMustLieInTheFilterSource()
    {
        var foreign = DavidFilter() with
        {
            Structured = DavidFilter().Structured! with { From = new TableIdentifier("other_source", "conf", "client") }
        };
        Should.Throw<ArgumentException>(() => foreign.Validate());
    }

    [Fact]
    public void Supersedes_MustNotReferenceItself()
    {
        Should.Throw<ArgumentException>(() => (DavidFilter() with { Supersedes = ["nicht_ausgelieferte_krane"] }).Validate());
        Should.NotThrow(() => (DavidFilter() with { Supersedes = ["anderer_filter"] }).Validate());
    }

    [Fact]
    public void Profile_Uncovered_IsMandatory()
    {
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Uncovered = null }).Validate());
    }

    [Fact]
    public void Profile_ScopeAddressesObjects_AndGranteeMustMatchItsType()
    {
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Scope = "a.b" }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Scope = "lwetem_prod.*.*.client_id" }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { GranteeSid = null }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { GranteeType = GranteeType.Role, GranteeSid = null, RoleName = null }).Validate());
        Should.NotThrow(() => (DavidProfile() with { GranteeType = GranteeType.Role, GranteeSid = null, RoleName = "Analyst" }).Validate());
    }

    [Fact]
    public void Profile_NeedsBindings_EachValid_AndNotTwiceOnTheSameTarget()
    {
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Bindings = [] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Bindings = [new FilterBinding { FilterName = "f", TargetPattern = "a.b" }] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Bindings = [new FilterBinding { FilterName = "f", ObjectKinds = FilterObjectKinds.None }] }).Validate());
        Should.Throw<ArgumentException>(() => (DavidProfile() with { Bindings = [new FilterBinding { FilterName = "f" }, new FilterBinding { FilterName = "f" }] }).Validate());
        Should.NotThrow(() => (DavidProfile() with { Bindings = [new FilterBinding { FilterName = "f" }, new FilterBinding { FilterName = "f", TargetPattern = "lwetem_prod.fms.*" }] }).Validate());
        new FilterBinding { FilterName = "f" }.ObjectKinds.ShouldBe(FilterObjectKinds.Relation | FilterObjectKinds.ProcedureResult);
    }

    [Fact]
    public void Binding_ColumnMap_TargetsSimpleNames()
    {
        Should.NotThrow(() => new FilterBinding { FilterName = "f", ColumnMap = new Dictionary<string, string> { ["client_id"] = "cid" } }.Validate());
        Should.Throw<ArgumentException>(() => new FilterBinding { FilterName = "f", ColumnMap = new Dictionary<string, string> { ["client_id"] = "c id" } }.Validate());
    }

    [Fact]
    public void DefinitionHash_IsStableAndChangesWithTheDefinition()
    {
        DavidFilter().ComputeDefinitionHash().ShouldBe(DavidFilter().ComputeDefinitionHash());
        (DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }).ComputeDefinitionHash().ShouldNotBe(DavidFilter().ComputeDefinitionHash());
        DavidProfile().ComputeDefinitionHash().ShouldBe(DavidProfile().ComputeDefinitionHash());
        (DavidProfile() with { Uncovered = UncoveredPolicy.Skip }).ComputeDefinitionHash().ShouldNotBe(DavidProfile().ComputeDefinitionHash());
        (DavidProfile() with { Bindings = [new FilterBinding { FilterName = "nicht_ausgelieferte_krane", TimeColumn = "ts" }] })
            .ComputeDefinitionHash().ShouldNotBe(DavidProfile().ComputeDefinitionHash());
    }
}
