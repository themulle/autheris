namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayRoleEnumRatchetTests
{
    private static readonly HashSet<string> AllowedCanonicalRoleNames = new(StringComparer.Ordinal)
    {
        "ClusterAdmin",
        "TenantAdmin",
        "GovernanceAdmin",
        "SecurityAuditor",
        "SchemaPublisher",
        "Consumer",
        "FilterAdmin",
        "FilterSync",
        "CallerAttributeApprover",
        "CallerAttributeInspector",
        "DataProductAdmin",
        "DataProductSync",
        "CatalogArtifactUploader",
        "DeputySync",
    };

    [Fact]
    public void Enum_values_are_a_subset_of_the_pre_1009_set()
    {
        var actualNames = Enum.GetNames<GatewayRole>();

        actualNames.ShouldNotContain("DataOwner");
        actualNames.ShouldNotContain("DataSteward");

        foreach (var name in actualNames)
        {
            AllowedCanonicalRoleNames.ShouldContain(name, $"Unexpected GatewayRole enum value '{name}'.");
        }
    }
}
