using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Api.Configuration;
using Autheris.Api.Endpoints;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Catalog;

public sealed class OpenSchemaRemovalTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static IWebHostEnvironment Env(string name)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Gateway:OpenSchema", "true")]
    [InlineData("Gateway:OpenSchema", "false")]
    [InlineData("Gateway:Catalog:OpenSchema", "true")]
    [InlineData("Gateway:Catalog:OpenSchema", "false")]
    [InlineData("Gateway__OpenSchema", "true")]
    [InlineData("Gateway__Catalog__OpenSchema", "true")]
    public void LegacyOpenSchemaKey_Set_StartupFails(string key, string value)
    {
        // OD-19 / SEC-DISC-01: Legacy OpenSchema configuration fails startup on presence in all environments.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [key.Replace("__", ":", StringComparison.Ordinal)] = value
        }).Build();

        var ex = Should.Throw<InvalidOperationException>(() => LegacySwitchGuard.ThrowIfLegacyKeysSet(config));
        ex.Message.ShouldContain("OpenSchema was removed (OD-19)");
        ex.Message.ShouldContain("Authenticated users discover the catalog by default");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ActiveBypasses_ContainNoOpenSchemaEntry()
    {
        var options = new GatewayOptions();
        options.GetAllActiveBypasses().ShouldNotContain(b => b.Contains("open_schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quickstart_ExpansionHasNoOpenSchema()
    {
        DevConfiguration.QuickstartExpansion.ShouldNotContain("OpenSchema");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OpenApiEndpoints_AnonymousOutsideDev_RequireAuthorization(string environment)
    {
        var env = Env(environment);
        var anonymous = new DefaultHttpContext();

        ODataEndpoints.RequiresSwaggerChallenge(new GatewayOptions(), env, anonymous).ShouldBeTrue();
        ODataEndpoints.RequiresMetadataChallenge(new GatewayOptions(), env, anonymous).ShouldBeTrue();

        var authResult = ODataEndpoints.CheckSwaggerAuth(new GatewayOptions(), env, anonymous);
        authResult.ShouldNotBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async System.Threading.Tasks.Task GraphQlCatalog_Anonymous_ReturnsUnauthorized()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) });
        var tables = Substitute.For<Autheris.Application.Common.Interfaces.ITableMetadataRepository>();
        var consents = Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>();
        var query = new Autheris.GraphQL.Types.Query();

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(() => query.GetCatalogAsync(
            domain: null, first: null, after: null, search: null, tag: null,
            tables, consents, approvalRepository: null, dataOwnershipRepository: null,
            accessor));

        ex.Errors.ShouldContain(e => e.Code == "UNAUTHORIZED");
    }
}
