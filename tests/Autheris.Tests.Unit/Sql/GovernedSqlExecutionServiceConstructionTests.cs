namespace Autheris.Tests.Unit.Sql;

using System.Reflection;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using TrinoSqlEngine;
using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

/// <summary>L-A3 (later item): the provider execution guard has no silent pass-through fallback in the service either.</summary>
public sealed class GovernedSqlExecutionServiceConstructionTests
{
    [Fact]
    public void EveryPublicConstructor_RequiresTheProviderExecutionGuard()
    {
        var constructors = typeof(GovernedSqlExecutionService).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        constructors.ShouldNotBeEmpty();

        foreach (var constructor in constructors)
        {
            var guard = constructor.GetParameters().SingleOrDefault(p => p.ParameterType == typeof(IProviderExecutionGuard));
            guard.ShouldNotBeNull();
            guard.IsOptional.ShouldBeFalse("a missing guard must not turn into PassThroughExecutionGuard (SQLite statements would run without the timeout interrupt)");
            guard.HasDefaultValue.ShouldBeFalse();
        }
    }

    [Fact]
    public void ANullGuard_IsRejected()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());

        Should.Throw<ArgumentNullException>(() => global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(options, global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: null!, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance))
            .ParamName.ShouldBe("executionGuard");
    }

    [Fact]
    public void NoPublicConstructor_TakesIServiceProvider()
    {
        // R-4: the service-locator parameter is gone; the federated service arrives lazily through the constructor.
        foreach (var constructor in typeof(GovernedSqlExecutionService).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            constructor.GetParameters().ShouldNotContain(p => p.ParameterType == typeof(IServiceProvider));
        }
    }

    [Fact]
    public void PublicConstructor_HasAtMostEightParameters()
    {
        typeof(GovernedSqlExecutionService).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .ShouldAllBe(c => c.GetParameters().Length <= 8);
    }

    [Fact]
    public async Task CrossSourceQuery_WithoutFederatedService_FailsClosed()
    {
        var orders = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, SourceName = "crm", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "tracking_id", DataType = "varchar" }]
        };
        var shipments = new TableMetadata
        {
            Identifier = new TableIdentifier("logistics", "v1", "shipments"),
            Table = new Table
            {
                TableName = "shipments", SchemaName = "v1", SourceType = "Http", DataSourceType = DataSourceType.HttpDeclarative, SourceName = "logistics", IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor { BaseUrl = "https://api.logistics.internal", PathTemplate = "/shipments", CompleteResponse = true }
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "status", DataType = "varchar" }]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(orders.Identifier, Arg.Any<CancellationToken>()).Returns(orders);
        repo.GetTableMetadataAsync(shipments.Identifier, Arg.Any<CancellationToken>()).Returns(shipments);
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "crm",
                AllowedDataSources = ["crm", "logistics"],
                CrossSource = new CrossSourceOptions { Enabled = true, AllowedTransports = ["WebSql", "Trino"] }
            }
        };

        // No federated service is supplied (federatedExecution: null): the router asks for it and must fail closed.
        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, global::Autheris.Tests.Unit.GovernedTestSupport.Guard,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            sqlEngine: FastSqlEngine.Default, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"), new Claim("tenant_id", "tenant_test")], "Test"));
        var request = new GovernedSqlQueryRequest(
            "SELECT o.id, s.status FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id", Transport: "WebSql");

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteQueryBufferedAsync(request, user, new TenantId("tenant_test")));
        ex.Message.ShouldContain("not configured");
    }
}
