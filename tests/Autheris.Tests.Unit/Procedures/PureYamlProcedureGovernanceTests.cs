namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Procedures.Tools;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class PureYamlProcedureGovernanceTests
{
    private static readonly TenantId Tenant = new("tenant-pure-yaml");

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-1001"),
            new(ClaimTypes.NameIdentifier, "user-123")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void ParseYaml_WithSourceTableAndColumn_PopulatesDeclaredOutputSources()
    {
        const string yaml = """
            name: get_orders
            procedure: api.usp_GetOrders
            validation: declared
            result_table: sales.orders
            referenced_tables:
              - sales.orders
              - sales.customers
            integrity:
              ddl_hash: "sha256:abc12345"
            outputs:
              - name: order_id
                type: int
                source_table: sales.orders
                source_column: id
              - name: customer_name
                type: varchar(100)
                source: sales.customers.name
              - plain_col
            """;

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "orders", allowRlsNone: false, maxTimeoutSeconds: 60);

        def.Name.ShouldBe("get_orders");
        def.ProcedureName.ShouldBe("api.usp_GetOrders");
        def.ValidationMode.ShouldBe(ProcedureValidationMode.Declared);
        def.DdlHash.ShouldBe("sha256:abc12345");
        def.ReferencedTables.ShouldBe(["sales.orders", "sales.customers"]);

        def.DeclaredOutputs.ShouldBe(["order_id", "customer_name", "plain_col"]);
        def.DeclaredOutputSources.ShouldNotBeNull();
        def.DeclaredOutputSources.Count.ShouldBe(2);

        var src1 = def.DeclaredOutputSources["order_id"];
        src1.Schema.ShouldBe("sales");
        src1.Table.ShouldBe("orders");
        src1.Column.ShouldBe("id");

        var src2 = def.DeclaredOutputSources["customer_name"];
        src2.Schema.ShouldBe("sales");
        src2.Table.ShouldBe("customers");
        src2.Column.ShouldBe("name");
    }

    [Fact]
    public async Task RegistrationService_InDeclaredMode_ActivatesAndPopulatesResultColumnSources()
    {
        const string yaml = """
            name: get_orders
            procedure: api.usp_GetOrders
            validation: declared
            result_table: sales.orders
            referenced_tables:
              - sales.orders
            outputs:
              - name: order_id
                type: int
                source_table: sales.orders
                source_column: id
            """;

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "orders", allowRlsNone: false, 60);
        var registry = new InMemoryProcedureRegistry();
        registry.Register(def);

        var loader = new ProcedureDefinitionLoader(
            registry,
            Options.Create(new GatewayOptions()),
            Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = new ProcedureRegistrationService(
            loader,
            registry,
            scopeFactory,
            Options.Create(new GatewayOptions()));

        await service.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("get_orders", out var registered).ShouldBeTrue();
        registered!.State.ShouldBe(ProcedureState.Active);
        registered.Validation.ShouldNotBeNull();
        registered.Validation!.IsValid.ShouldBeTrue();
        registered.Validation.ResultColumns.ShouldBe(["order_id"]);
        registered.Validation.ReferencedTables.ShouldBe(["sales.orders"]);
        registered.Validation.ResultColumnSources.ShouldNotBeNull();
        registered.Validation.ResultColumnSources!["order_id"].Table.ShouldBe("orders");
        registered.Validation.ResultColumnSources!["order_id"].Column.ShouldBe("id");
    }

    [Fact]
    public async Task Execute_DeclaredProcedure_AppliesSourceTableMaskingAndDeny()
    {
        const string yaml = """
            name: get_orders
            procedure: api.usp_GetOrders
            validation: declared
            result_table: sales.orders
            referenced_tables:
              - sales.orders
            outputs:
              - name: order_id
                type: int
                source_table: sales.orders
                source_column: id
              - name: sensitive_note
                type: varchar(255)
                source_table: sales.orders
                source_column: note
            """;

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "orders", allowRlsNone: false, 60);
        var registry = new InMemoryProcedureRegistry();
        registry.Register(def);

        var validation = new ProcedureValidationResult(
            IsValid: true,
            Errors: [],
            ResultColumns: def.DeclaredOutputs,
            ReferencedTables: ["sales.orders"],
            ParameterSqlTypes: new Dictionary<string, string>(),
            ResultColumnSources: def.DeclaredOutputSources);

        registry.MarkActive(def.Name, validation);

        var invoker = Substitute.For<IProcedureInvoker>();
        invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                Columns: ["order_id", "sensitive_note", "unauthorized_leak"],
                Rows:
                [
                    new object?[] { 100, "secret financial data", "should be dropped" }
                ],
                Truncated: false));

        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = new TableIdentifier("default", "sales", "orders"),
                Columns =
                [
                    new TableColumn { ColumnName = "id" },
                    new TableColumn { ColumnName = "note" }
                ]
            });

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());

        var resolution = Substitute.For<IConsentResolutionService>();
        var columnDecisions = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["note"] = ColumnAccessLevel.Mask
        };
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), columnDecisions, null));

        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***MASKED***");

        var opt = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"]
                }
            }
        });

        var audit = Substitute.For<IAuditLogRepository>();
        var executionService = new GovernedProcedureExecutionService(
            registry,
            invoker,
            opt,
            tables,
            consents,
            resolution,
            masking,
            audit: audit);

        var result = await executionService.ExecuteAsync("get_orders", null, User("Reader"), Tenant);

        // 1. Column governance: sensitive_note must be masked
        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["order_id"].ShouldBe(100);
        result.Rows[0]["sensitive_note"].ShouldBe("***MASKED***");

        // 2. Fail-closed: undeclared column 'unauthorized_leak' must be removed
        result.Columns.ShouldNotContain("unauthorized_leak");
        result.Rows[0].ContainsKey("unauthorized_leak").ShouldBeFalse();
    }

    [Fact]
    public void ProcedureYamlGenerator_GeneratesValidYaml_MatchingParser()
    {
        var generator = new ProcedureYamlGenerator();
        var request = new ProcedureGenerationRequest(
            Name: "get_customer_metrics",
            ProcedureName: "api.usp_GetCustomerMetrics",
            Summary: "Calculates customer KPIs",
            Parameters:
            [
                new ProcedureParameter("customer_id", "int", typeof(int), IsRequired: true, Description: "Customer ID")
            ],
            ContextBindings:
            [
                new ProcedureContextBinding(ProcedureContextKey.TenantId, "@tenant_id")
            ],
            Outputs:
            [
                new ProcedureOutputColumnInfo("customer_id", "int", "sales", "customers", "id"),
                new ProcedureOutputColumnInfo("lifetime_value", "decimal(18,2)", "sales", "orders", "total_amount")
            ],
            ReferencedTables: ["sales.customers", "sales.orders"],
            ResultTable: "sales.customers",
            DdlHash: "sha256:fedcba9876543210",
            TimeoutSeconds: 45,
            RequiredRoles: ["Analyst", "Admin"]);

        string yaml = generator.GenerateYaml(request);

        yaml.ShouldContain("name: get_customer_metrics");
        yaml.ShouldContain("procedure: api.usp_GetCustomerMetrics");
        yaml.ShouldContain("validation: declared");
        yaml.ShouldContain("ddl_hash: \"sha256:fedcba9876543210\"");
        yaml.ShouldContain("source_table: sales.customers");
        yaml.ShouldContain("source_column: id");

        // Verify that the generated YAML can be parsed cleanly by ProcedureDefinitionParser
        var def = ProcedureDefinitionParser.ParseYaml(yaml, "fallback", allowRlsNone: false, maxTimeoutSeconds: 60);
        def.Name.ShouldBe("get_customer_metrics");
        def.ValidationMode.ShouldBe(ProcedureValidationMode.Declared);
        def.DdlHash.ShouldBe("sha256:fedcba9876543210");
        def.ReferencedTables.ShouldBe(["sales.customers", "sales.orders"]);
        def.DeclaredOutputs.ShouldBe(["customer_id", "lifetime_value"]);
        def.DeclaredOutputSources["customer_id"].Column.ShouldBe("id");
        def.DeclaredOutputSources["lifetime_value"].Table.ShouldBe("orders");
    }
}
