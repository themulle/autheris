using System;
using System.Reflection;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

[Trait("Category", "Security")]
public sealed class PermissionTargetTests
{
    [Fact]
    public void Request_and_grant_kind_enum_values_are_stable()
    {
        ((int)ConsentRequestKind.Data).ShouldBe(0);
        ((int)ConsentRequestKind.ProfileAssignment).ShouldBe(1);
        ((int)ConsentRequestKind.Unmask).ShouldBe(2);
        ((int)ConsentRequestKind.Catalog).ShouldBe(3);

        ((int)PermissionGrantKind.Unknown).ShouldBe(0);
        ((int)PermissionGrantKind.Data).ShouldBe(1);
        ((int)PermissionGrantKind.Unmask).ShouldBe(2);
        ((int)PermissionGrantKind.Catalog).ShouldBe(3);

        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown(null).ShouldBe(PermissionGrantKind.Unknown);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("BOGUS").ShouldBe(PermissionGrantKind.Unknown);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("DATA").ShouldBe(PermissionGrantKind.Data);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("UNMASK").ShouldBe(PermissionGrantKind.Unmask);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("CATALOG").ShouldBe(PermissionGrantKind.Catalog);
    }

    [Fact]
    public void Targets_construct_for_table_container_and_profile()
    {
        var table = new TableIdentifier("sales", "dbo", "orders");
        var tableTarget = PermissionTarget.ForTable(table, ["email", "phone"]);
        tableTarget.HasTable.ShouldBeTrue();
        tableTarget.HasColumns.ShouldBeTrue();
        tableTarget.HasContainer.ShouldBeFalse();
        tableTarget.HasProfile.ShouldBeFalse();

        var containerId = Guid.NewGuid();
        var containerTarget = PermissionTarget.ForContainer(ContainerKind.DataProduct, containerId);
        containerTarget.HasContainer.ShouldBeTrue();
        containerTarget.HasTable.ShouldBeFalse();

        var profileId = Guid.NewGuid();
        var profileTarget = PermissionTarget.ForProfile(profileId);
        profileTarget.HasProfile.ShouldBeTrue();
        profileTarget.HasTable.ShouldBeFalse();
        profileTarget.HasContainer.ShouldBeFalse();
    }

    [Fact]
    public void External_commands_expose_no_SelfGrant_property()
    {
        typeof(PermissionDecisionCommand).GetProperty("SelfGrant", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            .ShouldBeNull();
        typeof(PermissionRequestSubmission).GetProperty("SelfGrant", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            .ShouldBeNull();
    }
}
