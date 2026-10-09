namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using Autheris.Application.Connectors;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 5: sources that filter rows in memory (HTTP connectors, Lakehouse, Delta) cannot evaluate the
/// filter's subquery. They answer 403 with a reason instead of an empty result that looks like "no data".
/// </summary>
public sealed class VirtualFilterInMemorySourcesTests
{
    private static readonly TableMetadata Meta = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id");

    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows =
        [new Dictionary<string, object?> { ["id"] = 1, ["client_id"] = 7 }];

    private static readonly GovernedRowPolicy Policy = new(NSubstitute.Substitute.For<Autheris.Domain.Interfaces.IColumnMaskingProvider>(), null, MaskingDisabled: true);

    private static TableAccessDecision Allowed() =>
        TableAccessDecision.Allowed(Meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

    [Fact]
    public void InMemoryFilter_WithVirtualFilter_IsRefused()
    {
        var decision = Allowed().WithMandatoryPredicate("EXISTS (SELECT 1 FROM client)", ["filter_a"]);

        var ex = Should.Throw<GatewayForbiddenException>(() => GovernedConnectorReader.Apply(Rows, Meta, decision, "t", rlsPushdownExecuted: false, inDbMaskingExecuted: false, Policy));
        ex.Message.ShouldContain("Virtual filters");
    }

    [Fact]
    public void PushedDownFilter_WithVirtualFilter_IsFine()
    {
        var decision = Allowed().WithMandatoryPredicate("EXISTS (SELECT 1 FROM client)", ["filter_a"]);

        GovernedConnectorReader.Apply(Rows, Meta, decision, "t", rlsPushdownExecuted: true, inDbMaskingExecuted: false, Policy).Count.ShouldBe(1);
    }

    [Fact]
    public void InMemoryFilter_WithoutVirtualFilter_IsUnchanged()
    {
        GovernedConnectorReader.Apply(Rows, Meta, Allowed(), "t", rlsPushdownExecuted: false, inDbMaskingExecuted: false, Policy).Count.ShouldBe(1);
    }
}
