#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Regression tests for Remediation Round 3 of repeated security review findings
/// (RR-L6-01, RR-L6-02, RR-L6-03, RR-L3-01, RR-L3-02, RR-L3-04, RR-L3-05, RR-L3-06).
/// </summary>
public sealed class RepeatedReviewRound3Tests
{
    // =========================================================================
    // Layer 6: Privacy & Dynamic Masking
    // =========================================================================

    [Fact]
    public void RR_L6_01_ColumnMasking_DoesNotDetectHotelOrIntelAsPhone()
    {
        var provider = new ColumnMaskingProvider();
        var rule = new MaskingRule { RuleType = "REGEX" };

        // "hotel_id" or "intel_score" must NOT match as phone and reveal digits as phone
        var hotelResult = provider.MaskValue("hotel_id", "12345", rule)?.ToString();
        var intelResult = provider.MaskValue("intel_score", "98765", rule)?.ToString();

        // Length < 8 generic mask -> fully masked with '*'
        hotelResult.ShouldBe("*****");
        intelResult.ShouldBe("*****");
    }

    [Fact]
    public void RR_L6_01_ColumnMasking_ShortStrings_FullyMaskedWithoutPlaintextLeakage()
    {
        var provider = new ColumnMaskingProvider();
        var rule = new MaskingRule { RuleType = "REGEX" };

        // Short strings (e.g. 5-digit German ZIP code or PIN) must NOT retain first & last char
        var zipResult = provider.MaskValue("postal_code", "89073", rule)?.ToString();
        zipResult.ShouldBe("*****");

        // 8-character string retains at most 25% (1 char per side)
        var codeResult = provider.MaskValue("auth_code", "12345678", rule)?.ToString();
        codeResult.ShouldBe("1******8");
    }

    [Fact]
    public async Task RR_L6_02_WebSql_PreventInDbHmacKeyExposure_RedactsWithoutParameter()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("super-secret-master-key-32bytes!"));

        var table = new TableIdentifier("corp", "hr", "employees");
        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table { TableName = "employees", SchemaName = "hr", SourceName = "default", SourceType = "SqlServer" },
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(metadata));
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { metadata }));

        // Enable PreventInDbHmacKeyExposure
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault-key",
                PreventInDbHmacKeyExposure = true
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["default"] = new() { ConnectionString = "Server=dummy;", Provider = "SqlServer" }
                }
            }
        });

        var service = new GovernedSqlExecutionService(
            options,
            tableRepository: metaRepo,
            secretProvider: secretProvider,
            logger: NullLogger<GovernedSqlExecutionService>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-USER-1"),
            new Claim(ClaimTypes.PrimarySid, "S-1-USER-1"),
            new Claim("tenant_id", "tenant-alpha")
        }, "Bearer"));

        var securedSql = await service.RewriteSqlAsync("SELECT id, ssn FROM hr.employees", user, new TenantId("tenant-alpha"));
        
        // When PreventInDbHmacKeyExposure is enabled, fail-closed redaction is emitted and NO internal HMAC parameters are bound
        securedSql.ShouldNotContain("HASHBYTES");
        securedSql.ShouldNotContain("HMAC");
        securedSql.ShouldContain("'***'");
    }

    [Fact]
    public void RR_L6_03_GatewayExecutionService_CreateTenantScopedHmacRule_ScopesPerTenant()
    {
        var rule = new MaskingRule
        {
            Id = Guid.NewGuid(),
            RuleType = "HMAC",
            HmacKeyId = "customer-key"
        };

        var scoped = GatewayExecutionService.CreateTenantScopedHmacRule(rule, "tenant-t1", "default");

        scoped.ShouldNotBeNull();
        scoped.RuleType.ShouldBe("HMAC_SHA256");
        scoped.HmacKeyId.ShouldBe("customer-key|tenant:tenant-t1");
    }

    // =========================================================================
    // Layer 3: Protocol & Schema Gateway (DuckDB OLAP, Arrow, ReBAC, Options)
    // =========================================================================

    [Theory]
    [InlineData("SELECT 1; DROP TABLE orders;")]
    [InlineData("INSERT INTO orders VALUES (1);")]
    [InlineData("CREATE TABLE evil (x int);")]
    [InlineData("PRAGMA threads = 16;")]
    [InlineData("ATTACH 'remote.db';")]
    public async Task RR_L3_01_DuckDbOlap_RejectsDangerousCommandsAndMultiStatements(string dangerousSql)
    {
        var options = new DuckDbOlapOptions { Enabled = true, MaxMemory = "128MB", MaxThreads = 1 };
        var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance);

        var req = new OlapQueryRequest(dangerousSql, Array.Empty<OlapTableSource>());
        var ex = await Should.ThrowAsync<Exception>(() => engine.ExecuteOlapQueryAsync(req));
        ex.Message.ShouldContain("prohibited", Case.Insensitive);
    }

    [Fact]
    public async Task RR_L3_01_DuckDbOlap_RejectsTableGeneratorsWithoutStagedSources()
    {
        var options = new DuckDbOlapOptions { Enabled = true, MaxMemory = "128MB", MaxThreads = 1 };
        var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance);

        var req = new OlapQueryRequest("SELECT * FROM range(1000000000)", Array.Empty<OlapTableSource>());
        var ex = await Should.ThrowAsync<SecurityException>(() => engine.ExecuteOlapQueryAsync(req));
        ex.Message.ShouldContain("range", Case.Insensitive);
    }

    [Fact]
    public async Task RR_L3_02_DuckDbOlap_RebacCheck_UsesCanonicalQualifiedName()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var table = new TableIdentifier("analytics", "finance", "reports");
        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table { TableName = "reports", SchemaName = "finance" },
            Columns = new[] { new TableColumn { ColumnName = "id", DataType = "int" } }
        };
        metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(metadata));

        var rebac = Substitute.For<IRebacEvaluator>();
        rebac.IsEnabled.Returns(true);
        RebacCheckRequest? capturedRebac = null;
        rebac.CheckAsync(Arg.Do<RebacCheckRequest>(r => capturedRebac = r), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(new RebacCheckResult(false)));

        var context = new DefaultHttpContext();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IHostEnvironment)).Returns(env);
        context.RequestServices = serviceProvider;

        var secContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-U1"),
            TenantId = new TenantId("tenant-t1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        context.Items["SecurityPrincipalContext"] = secContext;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.PrimarySid, "S-1-U1"), new Claim("tenant_id", "tenant-t1") }, "Bearer"));

        var body = Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\",\"tableNames\":[\"analytics.finance.reports\"]}");
        context.Request.Body = new MemoryStream(body);

        var engine = Substitute.For<IDuckDbOlapEngine>();
        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var options = Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } });

        // Architecture 1: the endpoint's single access decision (the real resolver) applies ReBAC on the fully qualified
        // object id (POL-11); the endpoint has no check of its own.
        var accessResolver = new DefaultCrossDomainAccessResolver(
            Substitute.For<IConsentRepository>(), Substitute.For<IConsentResolutionService>(), options: options, rebacEvaluator: rebac);

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            context, engine, metadataRepo, connectorRegistry, accessResolver, masking, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        capturedRebac.ShouldNotBeNull();
        capturedRebac.Object.ShouldBe("table:analytics.finance.reports");
    }

    [Fact]
    public async Task RR_L3_04_DuckDbOlap_InProduction_HidesTableOracleAndDeniedReasons()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));

        var context = new DefaultHttpContext();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IHostEnvironment)).Returns(env);
        context.RequestServices = serviceProvider;

        var secContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-U1"),
            TenantId = new TenantId("tenant-t1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        context.Items["SecurityPrincipalContext"] = secContext;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("user_sid", "S-1-U1") }, "Bearer"));

        var body = Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\",\"tableNames\":[\"secret_unauthorized_table\"]}");
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();

        var engine = Substitute.For<IDuckDbOlapEngine>();
        var connectorRegistry = Substitute.For<IAutherisConnectorRegistry>();
        var accessResolver = Substitute.For<ICrossDomainAccessResolver>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var options = Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } });

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            context, engine, metadataRepo, connectorRegistry, accessResolver, masking, options, NullLoggerFactory.Instance);

        // Anti-oracle: returns 403 Forbidden with generic access denied (not 404 Table Not Found)
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = new StreamReader(context.Response.Body).ReadToEnd();
        responseText.ShouldContain("Access denied to table 'secret_unauthorized_table'");
        responseText.ShouldNotContain("not found in metadata catalog");
    }

    [Fact]
    public void RR_L3_05_ValidateGatewayOptions_RejectsIntrospectionInProductionWithoutOptIn()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_enable_introspection = true },
            AllowInsecureWarnFlagsInProduction = false
        };

        var ex = Should.Throw<Exception>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => null));
        ex.Message.ShouldContain("warn_enable_introspection");
    }

    [Fact]
    public void API_16_ValidateGatewayOptions_RejectsAllowAllCorsInProductionWithoutOptIn()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            Insecure = new InsecureGettingStartedOptions { warn_allow_all_cors_origins = true },
            AllowInsecureWarnFlagsInProduction = false
        };

        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env, _ => null));
        ex.Message.ShouldContain("warn_allow_all_cors_origins");
    }

    [Fact]
    public void RR_L3_06_TableIdentifierNormalizer_CanonicalizesArrowAndRebacObjects()
    {
        // 1-part table
        var tid1 = TableIdentifierNormalizer.Normalize("orders");
        tid1.Domain.ShouldBe("default");
        tid1.Schema.ShouldBe("public");
        tid1.TableName.ShouldBe("orders");
        tid1.ToQualifiedName().ShouldBe("public.orders");

        // 2-part schema.table
        var tid2 = TableIdentifierNormalizer.Normalize("finance.invoices");
        tid2.Domain.ShouldBe("default");
        tid2.Schema.ShouldBe("finance");
        tid2.TableName.ShouldBe("invoices");
        tid2.ToQualifiedName().ShouldBe("finance.invoices");

        // 3-part domain.schema.table
        var tid3 = TableIdentifierNormalizer.Normalize("erp.accounting.Ledger_Entries");
        tid3.Domain.ShouldBe("erp");
        tid3.Schema.ShouldBe("accounting");
        tid3.TableName.ShouldBe("ledger_entries"); // Postgres dialect case-folding
        tid3.ToQualifiedName().ShouldBe("accounting.ledger_entries");
    }
}
