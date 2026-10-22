namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedSqlExecutionParameterSecurityTests
{
    private const string Tenant = "tenant_test";
    private const string DataSource = "pg_source";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, "Admin")
            ],
            "Test"));

    private static TableMetadata CreateTable(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table
        {
            TableName = id.TableName,
            SchemaName = id.Schema,
            SourceName = DataSource,
            SourceType = "PostgreSql"
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "region", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private static GovernedSqlExecutionService CreateService(TableMetadata tableMeta)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                tableMeta.Identifier,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["region"] = ColumnAccessLevel.Clear
                },
                null,
                hasUnconstrainedColumnAllow: true));

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = DataSource,
                AllowedDataSources = [DataSource],
                DmlWriterRoles = ["Admin"]
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [DataSource] = new() { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=testdb" }
                }
            }
        };

        var factory = Substitute.For<ISqlConnectionFactory>();
        var conn = Substitute.For<DbConnection>();
        var cmd = Substitute.For<DbCommand>();
        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        reader.FieldCount.Returns(0);
        cmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reader));
        cmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        cmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        conn.CreateCommand().Returns(cmd);
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(conn));

        return global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard,
            policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: null);
    }

    [Fact]
    public async Task Client_Parameter_With_Internal_Prefix_Throws_WebSqlPolicyException()
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);

        var request = new GovernedSqlQueryRequest(
            Sql: "SELECT id FROM orders WHERE id = @__gql_internal",
            Parameters: new Dictionary<string, object?> { ["__gql_internal"] = 42 },
            DataSourceName: DataSource);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway parameter name");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RF107_07e_Client_Parameter_With_KeySet_Prefix_Throws_WebSqlPolicyException()
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);

        var request = new GovernedSqlQueryRequest(
            Sql: "SELECT id FROM orders WHERE id = @__rls_ks0_0",
            Parameters: new Dictionary<string, object?> { ["__rls_ks0_0"] = 42 },
            DataSourceName: DataSource);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway parameter name");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RF107_07e_Raw_Sql_With_KeySet_Prefix_Throws_WebSqlPolicyException()
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);

        var request = new GovernedSqlQueryRequest(
            Sql: "SELECT id FROM orders WHERE id = __rls_ks0_0",
            Parameters: null,
            DataSourceName: DataSource);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway identifier");
    }

    [Fact]
    public async Task Client_Parameter_With_Rls_Prefix_Throws_WebSqlPolicyException()
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);

        var request = new GovernedSqlQueryRequest(
            Sql: "SELECT id FROM orders WHERE id = @p_rls_0",
            Parameters: new Dictionary<string, object?> { ["p_rls_0"] = "US" },
            DataSourceName: DataSource);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway parameter name");
    }

    [Fact]
    public void RowFilterSqlBuilder_Uses_Internal_Gql_Prefix_For_Parameters()
    {
        var builder = new RowFilterSqlBuilder();
        var filter = new ConsentRowFilter
        {
            ColumnName = "region",
            Operator = "EQ",
            ValueJson = "\"EU\""
        };
        var parameters = new Dictionary<string, object?>();
        int paramIndex = 0;
        var sql = builder.FormatConditionParameterized(filter, DatabaseDialect.PostgreSql, parameters, ref paramIndex);

        sql.ShouldContain("__gql_rls_");
        parameters.ShouldNotBeEmpty();
        parameters.Keys.ShouldAllBe(k => k.StartsWith("@__gql_rls_"));
    }

    private const string Sentinel = "SENTINEL-280-VALUE";

    private static GovernedSqlQueryRequest Planned(
        string sql,
        IReadOnlyDictionary<string, object?>? gateway,
        IReadOnlyDictionary<string, object?>? client = null,
        bool planned = true) =>
        new(sql, client, DataSource, Transport: SqlTransports.Rest, PlannedTableRead: planned)
        {
            GatewayParameters = gateway,
        };

    [Fact]
    public void Issue280_PlannedRead_GatewayParameters_Eq_BindsValue_NeverInText()
    {
        var request = Planned(
            "SELECT id FROM orders WHERE region = @__gql_tf_0",
            new Dictionary<string, object?> { ["@__gql_tf_0"] = Sentinel });

        var merged = GovernedSqlExecutionService.MergeGatewayParameters(request);
        var text = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, merged, out var names);

        merged.ShouldNotBeNull();
        merged["__gql_tf_0"].ShouldBe(Sentinel);
        text.ShouldNotContain(Sentinel);
        text.ShouldContain("__param___gql_tf_0");
        text.ShouldNotContain("@__gql_tf_0");
        names.ShouldContain("__gql_tf_0");
    }

    [Fact]
    public void Issue280_PlannedRead_GatewayParameters_InAndBetween_Bind()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var request = Planned(
            "SELECT id FROM orders WHERE region IN (@__gql_tf_0, @__gql_tf_1) AND ts >= @__gql_tf_2 AND ts < @__gql_tf_3",
            new Dictionary<string, object?>
            {
                ["@__gql_tf_0"] = "north",
                ["@__gql_tf_1"] = "south",
                ["@__gql_tf_2"] = from,
                ["@__gql_tf_3"] = to,
            });

        var merged = GovernedSqlExecutionService.MergeGatewayParameters(request);

        merged.ShouldNotBeNull();
        merged.Count.ShouldBe(4);
        merged["__gql_tf_2"].ShouldBeOfType<DateTime>().ShouldBe(from);
        merged["__gql_tf_3"].ShouldBeOfType<DateTime>().ShouldBe(to);
    }

    [Fact]
    public void Issue280_MergeGatewayParameters_NullOrEmpty_ReturnsClientParametersUnchanged()
    {
        var client = new Dictionary<string, object?> { ["x"] = 1 };

        GovernedSqlExecutionService.MergeGatewayParameters(Planned("SELECT 1", null, client)).ShouldBeSameAs(client);
        GovernedSqlExecutionService.MergeGatewayParameters(Planned("SELECT 1", new Dictionary<string, object?>(), client)).ShouldBeSameAs(client);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("__gql_tf_0", true)]
    [InlineData("@__gql_rls_0", true)]
    [InlineData("p_rls_x", true)]
    [InlineData("rls_x", true)]
    [InlineData("p_pd_x", true)]
    [InlineData("__rls_ks", true)]
    [InlineData("__GQL_TF_0", true)]
    [InlineData("__gql_tf_0", false)]
    [InlineData("@__gql_rls_0", false)]
    [InlineData("p_rls_x", false)]
    [InlineData("__GQL_TF_0", false)]
    public async Task Issue280_ClientReservedName_StillRejected_WhenPlannedRead(string name, bool planned)
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);
        var request = new GovernedSqlQueryRequest(
            Sql: $"SELECT id FROM orders WHERE id = @{name.TrimStart('@')}",
            Parameters: new Dictionary<string, object?> { [name] = 1 },
            DataSourceName: DataSource,
            Transport: SqlTransports.Rest,
            PlannedTableRead: planned);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("reserved gateway parameter name");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayAndClientNameCollision_FailsClosed()
    {
        // Case-insensitive, '@' trimmed: a gateway name equal to a client name is never merged silently.
        var request = Planned(
            "SELECT id FROM orders",
            new Dictionary<string, object?> { ["@__GQL_TF_0"] = 1 },
            new Dictionary<string, object?> { ["__gql_tf_0"] = 2 });

        var ex = Should.Throw<SecurityException>(() => GovernedSqlExecutionService.MergeGatewayParameters(request));

        ex.Message.ShouldNotContain("__gql_tf_0");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayParameters_WithoutPlannedRead_FailsClosed()
    {
        var request = Planned(
            "SELECT id FROM orders WHERE region = @__gql_tf_0",
            new Dictionary<string, object?> { ["@__gql_tf_0"] = Sentinel },
            planned: false);

        var ex = Should.Throw<SecurityException>(() => GovernedSqlExecutionService.MergeGatewayParameters(request));

        ex.Message.ShouldNotContain(Sentinel);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue280_GatewayParameters_WithoutPlannedRead_ThroughService_FailsClosed()
    {
        var table = CreateTable(new TableIdentifier(DataSource, "public", "orders"));
        var service = CreateService(table);
        var request = Planned(
            "SELECT id FROM orders WHERE region = @__gql_tf_0",
            new Dictionary<string, object?> { ["@__gql_tf_0"] = Sentinel },
            planned: false);

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldNotContain(Sentinel);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayListValue_FailsClosed()
    {
        var request = Planned(
            "SELECT id FROM orders WHERE id = @__gql_tf_0",
            new Dictionary<string, object?> { ["@__gql_tf_0"] = new[] { 1, 2 } });

        Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.MergeGatewayParameters(request));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayNonScalarValue_FailsClosed()
    {
        Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.MergeGatewayParameters(
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_tf_0"] = new byte[] { 1 } })));
        Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.MergeGatewayParameters(
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_tf_0"] = new object() })));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayParameters_NotSettableOutsideApplication()
    {
        typeof(GovernedSqlQueryRequest)
            .GetProperty("GatewayParameters", BindingFlags.Instance | BindingFlags.Public)
            .ShouldBeNull();

        var property = typeof(GovernedSqlQueryRequest)
            .GetProperty("GatewayParameters", BindingFlags.Instance | BindingFlags.NonPublic);
        property.ShouldNotBeNull();
        property.GetMethod!.IsPublic.ShouldBeFalse();
        property.SetMethod!.IsPublic.ShouldBeFalse();
        typeof(GovernedSqlQueryRequest).GetConstructors().SelectMany(c => c.GetParameters())
            .ShouldNotContain(p => p.Name == "GatewayParameters");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_ApplicationInternalsVisibleTo_OnlyKnownTestAssemblies()
    {
        var visibleTo = typeof(GovernedSqlQueryRequest).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName.Split(',')[0])
            .ToList();

        visibleTo.ShouldBe(
            new[] { "Autheris.Tests.Unit", "Autheris.Tests.SqlCompilerGate", "Autheris.Benchmarks" },
            ignoreOrder: true);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("@__gql_rls_0")]
    [InlineData("@__gql_mask_x")]
    [InlineData("@__gql_vf_0")]
    [InlineData("@__gql_caller_a__0")]
    [InlineData("@region")]
    [InlineData("@__param_region")]
    [InlineData("")]
    [InlineData("@")]
    public void Issue280_GatewayParameterNamedLikePolicyOrMask_FailsClosed(string name)
    {
        var request = Planned(
            "SELECT id FROM orders",
            new Dictionary<string, object?> { [name] = Sentinel });

        var ex = Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.MergeGatewayParameters(request));

        ex.Message.ShouldNotContain(Sentinel);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_PolicyParameterValue_NotOverriddenByGatewayParameter()
    {
        // A policy slot name can never be carried by the gateway map (the merge rejects it), so the policy value stays the only source.
        var request = Planned(
            "SELECT id FROM orders WHERE region = @__gql_tf_0",
            new Dictionary<string, object?>
            {
                ["@__gql_tf_0"] = "north",
                ["@__gql_rls_0"] = "attacker-region",
            });

        Should.Throw<InvalidOperationException>(() => GovernedSqlExecutionService.MergeGatewayParameters(request));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayValues_NotInLogsAuditOrExceptionMessages()
    {
        var messages = new List<string>();
        var cases = new GovernedSqlQueryRequest[]
        {
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_tf_0"] = Sentinel }, planned: false),
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_tf_0"] = Sentinel }, new Dictionary<string, object?> { ["__gql_tf_0"] = 1 }),
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_tf_0"] = new[] { Sentinel } }),
            Planned("SELECT 1", new Dictionary<string, object?> { ["@__gql_rls_0"] = Sentinel }),
        };

        foreach (var request in cases)
        {
            try
            {
                GovernedSqlExecutionService.MergeGatewayParameters(request);
            }
            catch (Exception ex)
            {
                messages.Add(ex.ToString());
            }
        }

        messages.Count.ShouldBe(cases.Length);
        messages.ShouldAllBe(m => !m.Contains(Sentinel) && !m.Contains("__gql_tf_0") && !m.Contains("__gql_rls_0"));
        // The request's text form never prints the gateway values either.
        cases[0].ToString().ShouldNotContain(Sentinel);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayParameters_OverLimit_FailsClosed()
    {
        var gateway = new Dictionary<string, object?>();
        for (int i = 0; i <= TreeSqlCompiler.MaxParameters; i++)
        {
            gateway["@__gql_tf_" + i] = i;
        }

        Should.Throw<SecurityException>(() => GovernedSqlExecutionService.MergeGatewayParameters(
            Planned("SELECT 1", gateway)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue280_GatewayParameters_AtLimit_Succeeds()
    {
        var gateway = new Dictionary<string, object?>();
        for (int i = 0; i < TreeSqlCompiler.MaxParameters; i++)
        {
            gateway["@__gql_tf_" + i] = i;
        }

        GovernedSqlExecutionService.MergeGatewayParameters(Planned("SELECT 1", gateway))!
            .Count.ShouldBe(TreeSqlCompiler.MaxParameters);
    }
}
