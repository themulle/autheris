namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SQLA-05: the <c>-- @timeout</c> header of a declarative SQL endpoint reaches the governed request.</summary>
public sealed class SqlEndpointTimeoutTests
{
    private static SqlEndpointDefinition Parse(string header)
    {
        using var loader = new SqlEndpointLoader(new InMemorySqlEndpointRegistry());
        return loader.ParseSqlContent($"-- @name q\n{header}\nSELECT id FROM t", "fallback");
    }

    [Fact]
    public void Loader_TimeoutHeader_IsParsed_AndAbsentMeansNull()
    {
        Parse("-- @timeout: 5").TimeoutSeconds.ShouldBe(5);
        Parse("-- @summary x").TimeoutSeconds.ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Loader_NonPositiveTimeout_IsRejected(string value) =>
        Should.Throw<ArgumentException>(() => Parse($"-- @timeout: {value}"));

    [Fact]
    public async Task Execution_CarriesTheEndpointTimeoutInTheGovernedRequest()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition("q", "q", "SELECT id FROM t", TimeoutSeconds: 5));
        GovernedSqlQueryRequest? captured = null;
        var governed = Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => captured = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", [], [], 0, 0));
        var service = new SqlEndpointExecutionService(registry, governed, Options.Create(new GatewayOptions()));

        await service.ExecuteEndpointAsync("q", new Dictionary<string, object?>(), new ClaimsPrincipal(new ClaimsIdentity("Test")), new TenantId("tenant_1"));

        captured!.TimeoutSeconds.ShouldBe(5);
    }
}
