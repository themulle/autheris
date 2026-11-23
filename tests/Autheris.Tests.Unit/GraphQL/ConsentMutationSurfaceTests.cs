namespace Autheris.Tests.Unit.GraphQL;

using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.GraphQL.Types;
using HotChocolate;
using Shouldly;
using Xunit;

/// <summary>GraphQL exposes request submission and withdrawal only; decisions, revocation, renewal and self-grant are never reachable here.</summary>
[Trait("Category", "Security")]
public sealed class ConsentMutationSurfaceTests
{
    private static readonly Regex DecisionVerb = new(
        "Approve|Reject|RevokeConsent|Renew|SelfGrant|Decide",
        RegexOptions.CultureInvariant);

    [Fact]
    public void No_decide_or_revoke_mutation()
    {
        var exposed = typeof(Mutation)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<GraphQLIgnoreAttribute>() is null)
            .Select(m => m.Name)
            .ToList();

        exposed.ShouldContain(nameof(Mutation.RequestTableAccessAsync));
        exposed.ShouldContain(nameof(Mutation.WithdrawAccessRequestAsync));
        exposed.Where(n => DecisionVerb.IsMatch(n)).ShouldBeEmpty();
    }
}
