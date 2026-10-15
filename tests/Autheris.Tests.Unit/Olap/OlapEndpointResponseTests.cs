namespace Autheris.Tests.Unit.Olap;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07 review: a failure while writing the 200 response keeps the old contract (400, generic error).</summary>
public sealed class OlapEndpointResponseTests
{
    private sealed class Unserializable
    {
        public int Value => throw new InvalidOperationException("secret serializer detail");
    }

    [Fact]
    public async Task SerializationFailureOfTheSuccessBody_BecomesA400_NotA500()
    {
        var staging = Substitute.For<IOlapStagingService>();
        staging.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(), Arg.Any<OlapQueryCommand>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OlapStagingOutcome(200, Result: new OlapQueryResult(["x"], [new List<object?> { new Unserializable() }], 1, TimeSpan.Zero)));
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("user_sid", "S-1-5-21-U")], "Bearer"));
        context.Items["SecurityPrincipalContext"] = new Autheris.Domain.Security.SecurityPrincipalContext
        {
            UserSid = new Autheris.Domain.Common.Sid("S-1-5-21-U"),
            TenantId = new Autheris.Domain.Common.TenantId("tenant-1"),
            GroupSids = new HashSet<Autheris.Domain.Common.Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\"}"));
        context.Response.Body = new MemoryStream();

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            context, staging, Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } }), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        text.ShouldContain("Analytical query execution failed.");
        text.ShouldNotContain("secret serializer detail");
    }
}
