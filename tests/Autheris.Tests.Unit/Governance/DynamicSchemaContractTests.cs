namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Contracts;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
            includedClasses: [ProtectionClass.Public],
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
            excludedClasses: [ProtectionClass.Internal],
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
                    ["partner"] = new() { IncludedClasses = [ProtectionClass.Public], ExcludeInaccessible = true },
                    ["mobile"] = new() { IncludedClasses = [ProtectionClass.Internal], ExcludeInaccessible = true }
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
        partnerDef.IncludedClasses.ShouldContain(ProtectionClass.Public);
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
                    ["partner"] = new() { IncludedClasses = [ProtectionClass.Public] }
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
    [Theory]
    [InlineData("internal", null, StatusCodes.Status403Forbidden, null)]
    [InlineData(null, "internal", StatusCodes.Status403Forbidden, null)]
    [InlineData("partner", null, StatusCodes.Status200OK, "partner")]
    [InlineData(null, null, StatusCodes.Status200OK, "partner")]
    public async Task SchemaContractMiddleware_ClaimContract_CannotBeOverriddenByHeaderOrQuery(string? header, string? query, int expectedStatus, string? expectedContract)
    {
        // API-3: a partner bound to "partner" via claim must not switch to "internal" through header or query string.
        var options = new GatewayOptions
        {
            SchemaContracts = new SchemaContractsOptions
            {
                Enabled = true,
                Contracts = new Dictionary<string, SchemaContractDefinitionOptions>
                {
                    ["partner"] = new() { IncludedClasses = [ProtectionClass.Public] },
                    ["internal"] = new() { IncludedClasses = [ProtectionClass.Internal] }
                }
            }
        };
        var manager = new SchemaContractManager(Options.Create(options), NullLogger<SchemaContractManager>.Instance);
        var middleware = new SchemaContractMiddleware(next: _ => Task.CompletedTask, NullLogger<SchemaContractMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("contract", "partner")], "Test"))
        };
        if (header != null) context.Request.Headers["X-Gateway-Contract"] = header;
        if (query != null) context.Request.QueryString = new QueryString($"?contract={query}");

        await middleware.InvokeAsync(context, manager);

        context.Response.StatusCode.ShouldBe(expectedStatus);
        if (expectedContract != null)
        {
            context.Items[SchemaContractMiddleware.ContractItemKey].ShouldBe(expectedContract);
        }
    }

    [Fact]
    public void CatalogVisibility_FilterByContract_AllowedTables_FiltersTablesAccurately()
    {
        var t1 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public" }
        };
        var t2 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "invoices"),
            Table = new Table { TableName = "invoices", SchemaName = "public" }
        };

        var contract = new SchemaContractDefinition("partner", allowedTables: ["orders"]);

        var filtered = Autheris.Application.Services.CatalogVisibility.FilterByContract(new[] { t1, t2 }, contract);

        filtered.Count.ShouldBe(1);
        filtered[0].Identifier.TableName.ShouldBe("orders");
    }

    [Fact]
    public void CatalogVisibility_FilterByContract_ExcludedClasses_ExcludesConfidentialTables()
    {
        var t1 = new TableMetadata
        {
            Identifier = new TableIdentifier("hr", "public", "public_info"),
            Table = new Table { TableName = "public_info" }
        };
        var t2 = new TableMetadata
        {
            Identifier = new TableIdentifier("hr", "public", "salaries"),
            Table = new Table { TableName = "salaries" }
        };

        var tableClasses = new Dictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>
        {
            [t1.Identifier] = Autheris.Domain.Security.ProtectionClass.Public,
            [t2.Identifier] = Autheris.Domain.Security.ProtectionClass.Secret
        };
        var contract = new SchemaContractDefinition("external", excludedClasses: [Autheris.Domain.Security.ProtectionClass.Secret]);

        var filtered = Autheris.Application.Services.CatalogVisibility.FilterByContract([t1, t2], contract, tableClasses);

        filtered.Count.ShouldBe(1);
        filtered[0].Identifier.TableName.ShouldBe("public_info");
    }

    [Fact]
    public async Task TableAccessPolicy_WithAllowedTablesContract_DeniesUnallowedTableExecution()
    {
        var allowedTable = new TableIdentifier("sales", "public", "orders");
        var deniedTable = new TableIdentifier("sales", "public", "invoices");

        var contract = new SchemaContractDefinition("partner", allowedTables: ["orders"]);

        var consentRepo = NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = deniedTable,
            TenantId = new TenantId("tenant-1"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user-1"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(
            NSubstitute.Arg.Any<IEnumerable<Sid>>(),
            NSubstitute.Arg.Any<TableIdentifier>(),
            NSubstitute.Arg.Any<DateTimeOffset>(),
            NSubstitute.Arg.Any<TenantId?>(),
            NSubstitute.Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }));

        var policy = new Autheris.Application.Policy.TableAccessPolicy(
            consentRepo,
            new Autheris.Application.Services.ConsentResolutionService(),
            cacheService: Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options: new GatewayOptions(),
            mandatoryFilters: Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance);

        var tableMeta = new TableMetadata
        {
            Identifier = deniedTable,
            Table = new Table { TableName = "invoices", SchemaName = "public" }
        };

        var query = new Autheris.Application.Policy.TableAccessQuery(
            new Sid("user-1"),
            new TenantId("tenant-1"),
            new HashSet<Sid>(),
            new HashSet<string>(),
            tableMeta,
            Contract: contract);

        var decision = await policy.DecideAsync(query, default);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Schema Contract Denial"));
    }

    [Fact]
    public async Task TableAccessPolicy_WithExcludedClassesContract_DeniesSecretTableExecution()
    {
        var sensitiveTable = new TableIdentifier("hr", "public", "salaries");
        var contract = new SchemaContractDefinition("external", excludedClasses: [Autheris.Domain.Security.ProtectionClass.Secret]);

        var consentRepo = NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>();
        var policy = new Autheris.Application.Policy.TableAccessPolicy(
            consentRepo,
            new Autheris.Application.Services.ConsentResolutionService(),
            cacheService: Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options: new GatewayOptions(),
            mandatoryFilters: Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance);

        var tableMeta = new TableMetadata
        {
            Identifier = sensitiveTable,
            Table = new Table { TableName = "salaries", SchemaName = "public" }
        };

        var protection = new Autheris.Domain.Model.EffectiveProtection(
            new Autheris.Domain.Model.ContainerRef(Autheris.Domain.Model.ContainerKind.Domain, Guid.NewGuid()),
            null,
            Autheris.Domain.Security.ProtectionClass.Secret,
            Autheris.Domain.Security.ProtectionClass.Secret,
            null,
            new Dictionary<string, Autheris.Domain.Model.ColumnProtection>(),
            Autheris.Domain.Model.CatalogVisibilityMode.OnRequest);

        var query = new Autheris.Application.Policy.TableAccessQuery(
            new Sid("user-1"),
            new TenantId("tenant-1"),
            new HashSet<Sid>(),
            new HashSet<string>(),
            tableMeta,
            Contract: contract,
            Protection: protection);

        var decision = await policy.DecideAsync(query, default);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Schema Contract Denial") && r.Contains("SECRET"));
    }

    [Fact]
    public async Task TableAccessPolicy_WithContractManager_ResolvesClaimContractAndEnforcesAtRuntime()
    {
        var deniedTable = new TableIdentifier("sales", "public", "invoices");
        var options = new GatewayOptions
        {
            SchemaContracts = new SchemaContractsOptions
            {
                Enabled = true,
                DefaultContract = "default",
                Contracts = new Dictionary<string, SchemaContractDefinitionOptions>
                {
                    ["partner"] = new() { AllowedTables = ["orders"] }
                }
            }
        };

        var manager = new SchemaContractManager(Options.Create(options), NullLogger<SchemaContractManager>.Instance);

        var consentRepo = NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>();
        var policy = new Autheris.Application.Policy.TableAccessPolicy(
            consentRepo,
            new Autheris.Application.Services.ConsentResolutionService(),
            cacheService: Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            options: new GatewayOptions(),
            mandatoryFilters: Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance,
            contractManager: manager);

        var tableMeta = new TableMetadata
        {
            Identifier = deniedTable,
            Table = new Table { TableName = "invoices", SchemaName = "public" }
        };

        var claims = new[] { new System.Security.Claims.Claim("contract", "partner") };

        var query = new Autheris.Application.Policy.TableAccessQuery(
            new Sid("user-1"),
            new TenantId("tenant-1"),
            new HashSet<Sid>(),
            new HashSet<string>(),
            tableMeta,
            Claims: claims);

        var decision = await policy.DecideAsync(query, default);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Schema Contract Denial") && r.Contains("partner"));
    }
}
