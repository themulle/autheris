namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// API-15: outside Development an anonymous Swagger request is challenged (WWW-Authenticate), not answered with a bare
/// 401 that browsers cannot turn into a Kerberos/Negotiate login.
/// </summary>
public sealed class SwaggerChallengeApi15Tests
{
    private static IWebHostEnvironment Env(string name)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Fact]
    public void AnonymousOutsideDevelopment_RequiresChallenge()
    {
        var anonymous = new DefaultHttpContext();
        var authenticated = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "Negotiate")) };

        ODataEndpoints.RequiresSwaggerChallenge(new GatewayOptions(), Env(Environments.Production), anonymous).ShouldBeTrue();
        ODataEndpoints.RequiresSwaggerChallenge(new GatewayOptions(), Env(Environments.Production), authenticated).ShouldBeFalse();
        ODataEndpoints.RequiresSwaggerChallenge(new GatewayOptions(), Env(Environments.Development), anonymous).ShouldBeFalse();
    }

    [Fact]
    public async Task ChallengeResult_InvokesTheDefaultChallengeScheme()
    {
        var auth = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(auth).AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        await Results.Challenge().ExecuteAsync(context);

        await auth.Received(1).ChallengeAsync(context, null, Arg.Any<AuthenticationProperties?>());
    }
}
