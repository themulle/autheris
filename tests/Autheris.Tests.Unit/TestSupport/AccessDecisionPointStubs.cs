namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;

/// <summary>Access decision point doubles for consumers whose test never reaches the decision point or needs a fixed decision.</summary>
internal static class AccessDecisionPointStubs
{
    /// <summary>A decision point that denies every request; a consumer that wrongly depends on it fails closed.</summary>
    public static IAccessDecisionPoint Denying() =>
        Returning(r => TableAccessDecision.Denied(r.Object.Identifier, "Denied by the test decision point stub."));

    public static IAccessDecisionPoint Returning(Func<AccessRequest, TableAccessDecision> decide)
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<TableAccessDecision>(decide(ci.Arg<AccessRequest>())));
        return pdp;
    }
}
