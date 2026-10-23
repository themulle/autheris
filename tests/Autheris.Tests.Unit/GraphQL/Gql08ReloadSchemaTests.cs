namespace Autheris.Tests.Unit.GraphQL;

using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-08: reloadSchema has a cluster-wide effect and is reserved for the canonical ClusterAdmin.
/// </summary>
public sealed class Gql08ReloadSchemaTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task GQL_08_TenantGovernanceAdmin_CannotReloadSchema()
    {
        var bus = Substitute.For<IEventBus>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GOVADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "TestAuth"));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().ReloadSchemaAsync(bus, accessor));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await bus.DidNotReceiveWithAnyArgs().PublishAsync<object>(default!, default!, default);
    }
}
