#pragma warning disable CA2012

namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Regression tests for the WebSQL / SQL executor findings of the security review 2026-10-02
/// (C-01, C-03, H-10, H-13, M-10, M-20).
/// </summary>
public sealed class SecurityReview20261002WebSqlTests
{
    private const string Tenant = "tenant_a";
    private const string HmacSecretRef = "GQL-HMAC-SECRET-KEY";
    private const string HmacSecretValue = "super-secret-hmac-value-0123456789";

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ClaimsPrincipal CreateUser(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-WEBSQL-USER"),
            new("tenant_id", Tenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static TableMetadata CreateEmployeesMetadata(string? sourceName = null, string hmacRuleType = "REDACT")
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "employees"),
            Table = new Table
            {
                TableName = "employees",
                SchemaName = "public",
                SourceName = sourceName ?? string.Empty,
                SourceType = "PostgreSQL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = new MaskingRule { RuleType = hmacRuleType }
            }
        };
    }

    private static TableMetadata CreateOrdersMetadata()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
    }

    private static ITableMetadataRepository CreateRepository(params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                foreach (var t in tables)
                {
                    if (t.Identifier.Equals(id))
                    {
                        return Task.FromResult<TableMetadata?>(t);
                    }
                }
                return Task.FromResult<TableMetadata?>(null);
            });
        return repo;
    }

    private static IConsentRepository CreateConsentRepository()
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        return repo;
    }

    private static IConsentResolutionService CreateConsentResolution(Func<TableIdentifier, TableAccessDecision> decisionFactory)
    {
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => decisionFactory(ci.ArgAt<TableIdentifier>(3)));
        return resolution;
    }

    private static TableAccessDecision UnconstrainedAllow(TableIdentifier table, string? rowFilter = null) =>
        TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilter, hasUnconstrainedColumnAllow: true);

    private static IPolicyEnforcementService CreateCasbin(TableAccessDecision? fixedDecision = null)
    {
        var casbin = Substitute.For<IPolicyEnforcementService>();
        casbin.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        casbin.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<TableAccessDecision>(
                fixedDecision ?? UnconstrainedAllow(ci.Arg<SecurityEvaluationContext>().TargetTable)));
        return casbin;
    }

    private static IKeyVaultSecretProvider CreateSecretProvider()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes(HmacSecretValue));
        return provider;
    }

    private static GatewayOptions CreateOptions(
        bool enabled = true,
        bool allowDml = false,
        List<string>? dmlWriterRoles = null,
        List<string>? allowedDataSources = null)
    {
        return new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = enabled,
                AllowDml = allowDml,
                DmlWriterRoles = dmlWriterRoles ?? [],
                AllowedDataSources = allowedDataSources ?? [],
                DefaultMaxRows = 100,
                MaxAllowedRows = 500
            },
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = HmacSecretRef,
                HmacKeyId = "key-2026-q4"
            }
        };
    }

    private static GovernedSqlExecutionService CreateService(
        GatewayOptions? options = null,
        ITableMetadataRepository? repository = null,
        IConsentResolutionService? consentResolution = null,
        IConsentRepository? consentRepository = null,
        IPolicyEnforcementService? casbin = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditLogRepository? auditLog = null,
        bool withConsentServices = true)
    {
        return new GovernedSqlExecutionService(
            Options.Create(options ?? CreateOptions()),
            policyEnforcement: casbin,
            consentResolution: withConsentServices ? (consentResolution ?? CreateConsentResolution(t => UnconstrainedAllow(t))) : null,
            tableRepository: repository ?? CreateRepository(CreateEmployeesMetadata(), CreateOrdersMetadata()),
            auditLogRepository: auditLog,
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: withConsentServices ? (consentRepository ?? CreateConsentRepository()) : null,
            secretProvider: secretProvider);
    }

    // =========================================================================
    // C-01: SQL functions must not bypass RLS / masking / ABAC
    // =========================================================================

    [Fact]
    public async Task C01_TableLessQueryToXml_IsRejected()
    {
        var service = CreateService();

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, '')", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task C01_TableLessLiteralSelect_IsRejected()
    {
        var service = CreateService();

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT 1", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task C01_QueryToXmlNextToGovernedTable_IsRejectedByFunctionPolicy()
    {
        var service = CreateService();

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, ''), id FROM employees", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task C01_GovernedTableSelect_StillWorks_WithTenantFilter()
    {
        var service = CreateService();

        var secured = await service.RewriteSqlAsync("SELECT id, name FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("tenant_id = 'tenant_a'");
        secured.ShouldContain("LIMIT 100");
    }

    // =========================================================================
    // C-03: Consent model, catalog masking, metadata requirement, data source allowlist
    // =========================================================================

    [Fact]
    public void C03_WebSql_IsDisabledByDefault()
    {
        new GatewayOptions().WebSql.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task C03_WebSqlDisabled_RejectsEveryStatement()
    {
        var service = CreateService(options: new GatewayOptions());

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id FROM employees", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task C03_ConsentDeny_IsEnforced_EvenWhenCasbinAllows()
    {
        var consent = CreateConsentResolution(t => TableAccessDecision.Denied(t, "no consent"));
        var service = CreateService(consentResolution: consent, casbin: CreateCasbin());

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id FROM employees", CreateUser(), new TenantId(Tenant)));
        ex.Message.ShouldContain("denied");
        ex.Message.ShouldNotContain("no consent");
    }

    [Fact]
    public async Task C03_MissingConsentServices_FailsClosed()
    {
        var service = CreateService(withConsentServices: false);

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id FROM employees", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task C03_ConsentRowFilter_IsPushedDown()
    {
        var consent = CreateConsentResolution(t => UnconstrainedAllow(t, "region = 'EU'"));
        var service = CreateService(consentResolution: consent);

        var secured = await service.RewriteSqlAsync("SELECT id, name FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("region = 'EU'");
        secured.ShouldContain("tenant_id = 'tenant_a'");
    }

    [Fact]
    public async Task C03_CasbinUnconstrained_DoesNotUnmaskSensitiveCatalogColumn()
    {
        var service = CreateService(casbin: CreateCasbin());

        var secured = await service.RewriteSqlAsync("SELECT id, ssn FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("'***'");
    }

    [Fact]
    public async Task C03_ExplicitConsentClear_UnmasksSensitiveColumn()
    {
        var consent = CreateConsentResolution(t => TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Clear,
            ["region"] = ColumnAccessLevel.Clear,
            ["tenant_id"] = ColumnAccessLevel.Clear
        }));
        var service = CreateService(consentResolution: consent, casbin: CreateCasbin());

        var secured = await service.RewriteSqlAsync("SELECT id, ssn FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldNotContain("'***'");
    }

    [Fact]
    public async Task C03_CasbinColumnDeny_RestrictsConsentClear()
    {
        var table = new TableIdentifier("default", "public", "employees");
        var casbinDecision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["name"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: true);
        var service = CreateService(casbin: CreateCasbin(casbinDecision));

        var secured = await service.RewriteSqlAsync("SELECT id, name FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("NULL AS");
    }

    [Fact]
    public async Task C03_TableWithoutCatalogMetadata_IsRejected()
    {
        var service = CreateService();

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT * FROM hr_salaries", CreateUser(), new TenantId(Tenant)));
        ex.Message.ShouldContain("not registered");
    }

    [Fact]
    public async Task C03_DataSourceNotOnAllowlist_IsRejected()
    {
        var service = CreateService();

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(
                new GovernedSqlQueryRequest("SELECT id FROM employees", DataSourceName: "hr_prod"),
                CreateUser(),
                new TenantId(Tenant),
                (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task C03_DataSourceOnAllowlist_IsAccepted()
    {
        var service = CreateService(options: CreateOptions(allowedDataSources: ["analytics"]));
        bool rowWriterCalled = false;

        await service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("SELECT id FROM employees", DataSourceName: "analytics"),
            CreateUser(),
            new TenantId(Tenant),
            (_, _) =>
            {
                rowWriterCalled = true;
                return Task.CompletedTask;
            });

        rowWriterCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task C03_TableBoundToDifferentDataSource_IsRejected()
    {
        var repo = CreateRepository(CreateEmployeesMetadata(sourceName: "hr_db"));
        var service = CreateService(repository: repo);

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id FROM employees", CreateUser(), new TenantId(Tenant)));
    }

    // =========================================================================
    // H-10: Filter oracle on masked / sensitive columns
    // =========================================================================

    [Fact]
    public void H10_EffectiveAccess_SensitiveColumnWithoutExplicitClear_IsMask()
    {
        var metadata = CreateEmployeesMetadata();
        var decision = UnconstrainedAllow(metadata.Identifier);

        decision.GetEffectiveColumnAccess("ssn", metadata).ShouldBe(ColumnAccessLevel.Mask);
        decision.GetEffectiveColumnAccess("name", metadata).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void H10_EffectiveAccess_ExplicitClearAndDeny_AreRespected()
    {
        var metadata = CreateEmployeesMetadata();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["ssn"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Deny
        });

        decision.GetEffectiveColumnAccess("ssn", metadata).ShouldBe(ColumnAccessLevel.Clear);
        decision.GetEffectiveColumnAccess("name", metadata).ShouldBe(ColumnAccessLevel.Deny);
        decision.GetEffectiveColumnAccess("region", metadata).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task H10_SqlExecutor_FilterOnSensitiveColumn_WithUnconstrainedAllow_IsRejected()
    {
        var metadata = CreateEmployeesMetadata();
        var principal = CreateUser();
        var context = new DataSourceExecutionContext(
            SourceName: "hr_db",
            Metadata: metadata,
            Principal: principal,
            AccessDecision: UnconstrainedAllow(metadata.Identifier),
            Arguments: new Dictionary<string, object?> { ["ssn"] = "123-45-6789" },
            RequestedFields: ["id", "name"]);

        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("ssn");
    }

    [Fact]
    public async Task H10_SqlExecutor_FilterOnClearColumn_StillWorks()
    {
        var metadata = CreateEmployeesMetadata();
        var context = new DataSourceExecutionContext(
            SourceName: "hr_db",
            Metadata: metadata,
            Principal: CreateUser(),
            AccessDecision: UnconstrainedAllow(metadata.Identifier),
            Arguments: new Dictionary<string, object?> { ["name"] = "Alice" },
            RequestedFields: ["id", "name"]);

        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var rows = await executor.ExecuteAsync(context);
        rows.ShouldNotBeNull();
    }

    [Fact]
    public void H10_ConnectorEvaluator_FilterOnMaskingRuleColumn_IsRejected()
    {
        var metadata = CreateEmployeesMetadata();
        var session = new ConnectorSessionContext(
            Principal: CreateUser(),
            Tenant: new TenantId(Tenant),
            AccessDecision: UnconstrainedAllow(metadata.Identifier),
            ProjectedColumns: ["id"],
            Arguments: new Dictionary<string, object?> { ["ssn"] = "123-45-6789" });

        Should.Throw<SecurityException>(() => ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(session, metadata));
    }

    [Fact]
    public void H10_ConnectorEvaluator_FilterOnExplicitlyClearSensitiveColumn_IsAllowed()
    {
        var metadata = CreateEmployeesMetadata();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Clear
        });
        var session = new ConnectorSessionContext(
            Principal: CreateUser(),
            Tenant: new TenantId(Tenant),
            AccessDecision: decision,
            ProjectedColumns: ["id"],
            Arguments: new Dictionary<string, object?> { ["ssn"] = "123-45-6789" });

        Should.NotThrow(() => ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(session, metadata));
    }

    // =========================================================================
    // H-13: No secret name as salt, keyed HMAC with the resolved secret, never in SQL text / audit
    // =========================================================================

    [Fact]
    public async Task H13_WebSqlHmac_UsesParameterizedKey_AndNeverEmbedsSecretOrSecretName()
    {
        var repo = CreateRepository(CreateEmployeesMetadata(hmacRuleType: "HMAC"));
        var service = CreateService(repository: repo, secretProvider: CreateSecretProvider());

        var secured = await service.RewriteSqlAsync("SELECT id, ssn FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("HMAC(");
        secured.ShouldContain("@__gql_mk0");
        secured.ShouldNotContain(HmacSecretRef);
        secured.ShouldNotContain(HmacSecretValue);
        secured.ShouldNotContain("DIGEST(");
    }

    [Fact]
    public async Task H13_WebSqlAudit_DoesNotContainSecretOrSecretName()
    {
        var repo = CreateRepository(CreateEmployeesMetadata(hmacRuleType: "HMAC"));
        var audit = Substitute.For<IAuditLogRepository>();
        AuditLogEntry? captured = null;
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => captured = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var service = CreateService(repository: repo, secretProvider: CreateSecretProvider(), auditLog: audit);
        string? syntheticQueryEcho = null;

        await service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("SELECT id, ssn FROM employees"),
            CreateUser(),
            new TenantId(Tenant),
            async (reader, token) =>
            {
                if (await reader.ReadAsync(token))
                {
                    syntheticQueryEcho = reader.GetValue(1)?.ToString();
                }
            });

        captured.ShouldNotBeNull();
        captured.DetailsJson.ShouldNotContain(HmacSecretRef);
        captured.DetailsJson.ShouldNotContain(HmacSecretValue);
        syntheticQueryEcho.ShouldNotBeNull();
        syntheticQueryEcho.ShouldNotContain(HmacSecretValue);
    }

    [Fact]
    public async Task H13_WithoutResolvableSecret_HmacColumnIsRedacted_NotHashedUnkeyed()
    {
        var repo = CreateRepository(CreateEmployeesMetadata(hmacRuleType: "HMAC"));
        var service = CreateService(repository: repo, secretProvider: null);

        var secured = await service.RewriteSqlAsync("SELECT id, ssn FROM employees", CreateUser(), new TenantId(Tenant));

        secured.ShouldContain("'***'");
        secured.ShouldNotContain("HMAC(");
        secured.ShouldNotContain(HmacSecretRef);
    }

    [Fact]
    public async Task H13_ReservedInternalParameterName_InSqlOrParameters_IsRejected()
    {
        var service = CreateService(secretProvider: CreateSecretProvider());

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id, '__gql_mk0' FROM employees", CreateUser(), new TenantId(Tenant)));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(
                new GovernedSqlQueryRequest("SELECT id FROM employees", new Dictionary<string, object?> { ["@__gql_mk0"] = "attacker-key" }),
                CreateUser(),
                new TenantId(Tenant),
                (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void H13_SqlExecutor_MaskedHmacProjection_ContainsNoSaltOrUnkeyedHash()
    {
        var metadata = CreateEmployeesMetadata(hmacRuleType: "HMAC");

        var projection = SqlDataSourceExecutor.BuildMaskedColumnProjection("ssn", "varchar", DatabaseDialect.PostgreSql, metadata);

        projection.ShouldNotContain("DIGEST");
        projection.ShouldNotContain("HASHBYTES");
        projection.ShouldNotContain(HmacSecretRef);
        projection.ShouldNotContain("gateway_salt");
    }

    // =========================================================================
    // M-20: DML authorization and WITH CHECK against the caller's tenant
    // =========================================================================

    [Fact]
    public async Task M20_Dml_WithoutWriterRole_IsRejected()
    {
        var service = CreateService(options: CreateOptions(allowDml: true, dmlWriterRoles: ["WebSqlWriter"]));

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("DELETE FROM orders WHERE id = 1", CreateUser(), new TenantId(Tenant)));
        ex.Message.ShouldContain("writer role");
    }

    [Fact]
    public async Task M20_Dml_WithNoWriterRolesConfigured_IsRejected()
    {
        var service = CreateService(options: CreateOptions(allowDml: true));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("DELETE FROM orders WHERE id = 1", CreateUser("WebSqlWriter"), new TenantId(Tenant)));
    }

    [Fact]
    public async Task M20_Insert_IntoLibraryDefaultTenant42_IsRejected()
    {
        var service = CreateService(options: CreateOptions(allowDml: true, dmlWriterRoles: ["WebSqlWriter"]));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("INSERT INTO orders (id, amount, tenant_id) VALUES (1, 10, '42')", CreateUser("WebSqlWriter"), new TenantId(Tenant)));
    }

    [Fact]
    public async Task M20_Insert_WithoutTenantColumn_IsRejected()
    {
        var service = CreateService(options: CreateOptions(allowDml: true, dmlWriterRoles: ["WebSqlWriter"]));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("INSERT INTO orders (id, amount) VALUES (1, 10)", CreateUser("WebSqlWriter"), new TenantId(Tenant)));
    }

    [Fact]
    public async Task M20_Insert_IntoOwnTenant_WithWriterRole_IsAccepted()
    {
        var service = CreateService(options: CreateOptions(allowDml: true, dmlWriterRoles: ["WebSqlWriter"]));

        var secured = await service.RewriteSqlAsync("INSERT INTO orders (id, amount, tenant_id) VALUES (1, 10, 'tenant_a')", CreateUser("WebSqlWriter"), new TenantId(Tenant));

        secured.ShouldContain("INSERT");
    }

    // =========================================================================
    // M-10: No exception / database messages to the client
    // =========================================================================

    private static async Task<(int Status, string Body)> InvokeEndpointAsync(Exception toThrow)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            TraceIdentifier = "trace-websql-42",
            User = CreateUser()
        };
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT id FROM employees"));
        context.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            new ThrowingSqlService(toThrow),
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task M10_DatabaseError_IsNotEchoedToClient()
    {
        var (status, body) = await InvokeEndpointAsync(
            new InvalidOperationException("relation \"hr.salaries\" does not exist on server db-prod-01"));

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        body.ShouldNotContain("hr.salaries");
        body.ShouldNotContain("db-prod-01");
        body.ShouldContain("trace-websql-42");
    }

    [Fact]
    public async Task M10_UncuratedSecurityException_ReturnsGenericForbidden()
    {
        var (status, body) = await InvokeEndpointAsync(new SecurityException("internal policy rule p42 for subject S-1-5-21-ADMIN"));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldNotContain("p42");
        body.ShouldContain("trace-websql-42");
    }

    [Fact]
    public async Task M10_CuratedPolicyException_IsReturnedWith403()
    {
        var (status, body) = await InvokeEndpointAsync(new WebSqlPolicyException("DDL statements are strictly forbidden in WebSQL."));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("DDL statements");
    }

    [Fact]
    public async Task M10_ArgumentException_ReturnsGenericBadRequest()
    {
        var (status, body) = await InvokeEndpointAsync(new ArgumentException("line 1:7: mismatched input near 'secret_table'"));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldNotContain("secret_table");
    }

    private sealed class ThrowingSqlService(Exception toThrow) : IGovernedSqlExecutionService
    {
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default)
            => Task.FromException(toThrow);

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromException<GovernedSqlResult>(toThrow);

        public Task<string> RewriteSqlAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromException<string>(toThrow);
    }
}
