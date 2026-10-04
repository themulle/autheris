namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Contracts;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DynamicSchemaContractTests
{
    private const string SampleSdl = """
    type Query {
      customer(id: ID!): Customer
      internalMetrics: String @inaccessible
    }

    type Customer {
      id: ID!
      name: String! @tag(name: "partner") @tag(name: "public")
      email: String @tag(name: "partner")
      ssn: String @inaccessible
      internalRiskScore: Float @tag(name: "internal")
    }

    type InternalAuditLog @inaccessible {
      id: ID!
      details: String
    }

    type HighlyRestrictedData {
      secretToken: String @inaccessible
    }
    """;

    [Fact]
    public void SchemaContractFilter_StripsInaccessible_WhenExcludeInaccessibleIsTrue()
    {
        // Arrange
        var contract = new SchemaContractDefinition("public", excludeInaccessible: true);

        // Act
        var filteredSdl = SchemaContractFilter.FilterSchema(SampleSdl, contract);

        // Assert
        filteredSdl.ShouldNotContain("internalMetrics");
        filteredSdl.ShouldNotContain("ssn");
        filteredSdl.ShouldNotContain("InternalAuditLog");
        filteredSdl.ShouldNotContain("@inaccessible");
    }

    [Fact]
    public void SchemaContractFilter_FiltersByIncludedTags_ForPartnerContract()
    {
        // Arrange
        var contract = new SchemaContractDefinition(
            name: "partner",
            includedTags: new[] { "partner" },
            excludeInaccessible: true);

        // Act
        var filteredSdl = SchemaContractFilter.FilterSchema(SampleSdl, contract);

        // Assert
        filteredSdl.ShouldContain("name: String!");
        filteredSdl.ShouldContain("email: String");
        filteredSdl.ShouldNotContain("internalRiskScore");
        filteredSdl.ShouldNotContain("ssn");
    }

    [Fact]
    public void SchemaContractFilter_ExcludesFields_WithExcludedTags()
    {
        // Arrange
        var contract = new SchemaContractDefinition(
            name: "external",
            excludedTags: new[] { "internal" },
            excludeInaccessible: true);

        // Act
        var filteredSdl = SchemaContractFilter.FilterSchema(SampleSdl, contract);

        // Assert
        filteredSdl.ShouldContain("name: String!");
        filteredSdl.ShouldNotContain("internalRiskScore");
    }

    [Fact]
    public void SchemaContractFilter_PrunesOrphanTypes_WhenAllFieldsAreFiltered()
    {
        // Arrange
        var contract = new SchemaContractDefinition("strict_public", excludeInaccessible: true);

        // Act
        var filteredSdl = SchemaContractFilter.FilterSchema(SampleSdl, contract);

        // Assert: HighlyRestrictedData only had secretToken which is @inaccessible -> whole type pruned
        filteredSdl.ShouldNotContain("type HighlyRestrictedData");
    }

    [Fact]
    public void SchemaContractManager_HasContracts_AndResolvesSlice()
    {
        // Arrange
        var options = new GatewayOptions
        {
            SchemaContracts = new SchemaContractsOptions
            {
                Enabled = true,
                DefaultContract = "default",
                Contracts = new Dictionary<string, SchemaContractDefinitionOptions>
                {
                    ["partner"] = new() { IncludedTags = ["partner"], ExcludeInaccessible = true },
                    ["mobile"] = new() { IncludedTags = ["mobile"], ExcludeInaccessible = true }
                }
            }
        };

        var manager = new SchemaContractManager(Options.Create(options), NullLogger<SchemaContractManager>.Instance);

        // Act & Assert
        manager.IsEnabled.ShouldBeTrue();
        manager.HasContract("partner").ShouldBeTrue();
        manager.HasContract("mobile").ShouldBeTrue();
        manager.HasContract("default").ShouldBeTrue();
        manager.HasContract("unknown").ShouldBeFalse();

        var partnerDef = manager.GetContract("partner");
        partnerDef.ShouldNotBeNull();
        partnerDef.IncludedTags.ShouldContain("partner");
    }

    [Fact]
    public async Task SchemaContractMiddleware_RejectsUnknownContract_With400()
    {
        // Arrange
        var options = new GatewayOptions
        {
            SchemaContracts = new SchemaContractsOptions { Enabled = true }
        };
        var manager = new SchemaContractManager(Options.Create(options), NullLogger<SchemaContractManager>.Instance);

        var middleware = new SchemaContractMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<SchemaContractMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Gateway-Contract"] = "non_existent_contract";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context, manager);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var res = await reader.ReadToEndAsync();
        res.ShouldContain("INVALID_SCHEMA_CONTRACT");
    }

    [Fact]
    public async Task SchemaContractMiddleware_ResolvesContractFromHeader()
    {
        // Arrange
        var options = new GatewayOptions
        {
            SchemaContracts = new SchemaContractsOptions
            {
                Enabled = true,
                Contracts = new Dictionary<string, SchemaContractDefinitionOptions>
                {
                    ["partner"] = new() { IncludedTags = ["partner"] }
                }
            }
        };
        var manager = new SchemaContractManager(Options.Create(options), NullLogger<SchemaContractManager>.Instance);

        var middleware = new SchemaContractMiddleware(
            next: (ctx) => Task.CompletedTask,
            NullLogger<SchemaContractMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Gateway-Contract"] = "partner";

        // Act
        await middleware.InvokeAsync(context, manager);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Items[SchemaContractMiddleware.ContractItemKey].ShouldBe("partner");
        context.Response.Headers["X-Gateway-Contract"].ToString().ShouldBe("partner");
    }
}
