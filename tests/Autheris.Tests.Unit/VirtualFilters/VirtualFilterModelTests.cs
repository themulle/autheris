namespace Autheris.Tests.Unit.VirtualFilters;

using System;
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

    internal static FilterBinding DavidBinding(string filter = "nicht_ausgelieferte_krane") => new()
    {
        TenantId = Tenant,
        FilterName = filter,
        TargetPattern = "lwetem_prod.*.*.client_id",
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-LWE-DAVID"),
        OnUnmatched = OnUnmatched.Deny
    };

    [Fact]
    public void DavidCase_IsValid()
    {
        Should.NotThrow(() => DavidFilter().Validate());
        Should.NotThrow(() => DavidBinding().Validate());
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
    public void Binding_OnUnmatched_IsMandatory()
    {
        Should.Throw<ArgumentException>(() => (DavidBinding() with { OnUnmatched = null }).Validate());
    }

    [Fact]
    public void Binding_PatternMustParse_AndGranteeMustMatchItsType()
    {
        Should.Throw<ArgumentException>(() => (DavidBinding() with { TargetPattern = "a.b" }).Validate());
        Should.Throw<ArgumentException>(() => (DavidBinding() with { GranteeSid = null }).Validate());
        Should.Throw<ArgumentException>(() => (DavidBinding() with { GranteeType = GranteeType.Role, GranteeSid = null, RoleName = null }).Validate());
        Should.NotThrow(() => (DavidBinding() with { GranteeType = GranteeType.Role, GranteeSid = null, RoleName = "Analyst" }).Validate());
    }

    [Fact]
    public void Binding_ObjectKinds_MustNotBeEmpty()
    {
        Should.Throw<ArgumentException>(() => (DavidBinding() with { ObjectKinds = FilterObjectKinds.None }).Validate());
        DavidBinding().ObjectKinds.ShouldBe(FilterObjectKinds.Relation | FilterObjectKinds.ProcedureResult);
    }

    [Fact]
    public void Binding_ColumnMap_TargetsSimpleNames()
    {
        Should.NotThrow(() => (DavidBinding() with { ColumnMap = new System.Collections.Generic.Dictionary<string, string> { ["client_id"] = "cid" } }).Validate());
        Should.Throw<ArgumentException>(() => (DavidBinding() with { ColumnMap = new System.Collections.Generic.Dictionary<string, string> { ["client_id"] = "c id" } }).Validate());
    }

    [Fact]
    public void DefinitionHash_IsStableAndChangesWithTheDefinition()
    {
        DavidFilter().ComputeDefinitionHash().ShouldBe(DavidFilter().ComputeDefinitionHash());
        (DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }).ComputeDefinitionHash().ShouldNotBe(DavidFilter().ComputeDefinitionHash());
        DavidBinding().ComputeDefinitionHash().ShouldBe(DavidBinding().ComputeDefinitionHash());
        (DavidBinding() with { OnUnmatched = OnUnmatched.Skip }).ComputeDefinitionHash().ShouldNotBe(DavidBinding().ComputeDefinitionHash());
    }
}
