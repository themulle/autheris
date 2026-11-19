namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Access decision point doubles for consumers whose test never reaches the decision point or needs a fixed decision.</summary>
internal static class AccessDecisionPointStubs
{
    /// <summary>A decision point that denies every request; a consumer that wrongly depends on it fails closed.</summary>
    public static IAccessDecisionPoint Denying() =>
        Returning(r => TableAccessDecision.Denied(r.Object.Identifier, "Denied by the test decision point stub."));

    public static IAccessDecisionPoint Returning(Func<AccessRequest, TableAccessDecision> decide) =>
        new FuncAccessDecisionPoint(r => new ValueTask<TableAccessDecision>(decide(r)));

    /// <summary>A decision point whose decision is computed (or thrown) by <paramref name="decide"/>.</summary>
    public static IAccessDecisionPoint Throwing(Func<Exception> create) =>
        new FuncAccessDecisionPoint(_ => throw create());

    private sealed class FuncAccessDecisionPoint(Func<AccessRequest, ValueTask<TableAccessDecision>> decide) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) => decide(request);

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
