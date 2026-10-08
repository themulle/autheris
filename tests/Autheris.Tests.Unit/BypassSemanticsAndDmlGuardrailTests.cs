namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SEM_*: DANGER/WARN semantics of the security switches (DANGER blocks outside Development and is unhealthy,
/// WARN is permitted and reported, regular options are silent).
/// DML_*: WebSQL DML guardrails (unfiltered UPDATE/DELETE rejected, WebSql.MaxAffectedRows with rollback, DML audit).
/// </summary>
public sealed class BypassSemanticsAndDmlGuardrailTests
{
    private static readonly Func<string, string?> NoEnvironmentVariables = _ => null;

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static DataMaskingOptions ProdMasking() => new() { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" };

    // =========================================================================
    // SEM: classification
    // =========================================================================

    private static GatewayOptions WithNewDangerSwitch(string name) => name switch
    {
        "warn_allow_unmasked_ai_access" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_unmasked_ai_access = true }, DataMasking = ProdMasking() },
        "warn_mock_external_systems_if_unreachable" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_mock_external_systems_if_unreachable = true }, DataMasking = ProdMasking() },
        "warn_auto_approve_access_requests" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_auto_approve_access_requests = true }, DataMasking = ProdMasking() },
        "warn_disable_rate_limiting" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_disable_rate_limiting = true }, DataMasking = ProdMasking() },
        "warn_allow_unsigned_s3_requests" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true }, DataMasking = ProdMasking() },
        "warn_ignore_webhook_timestamp_tolerance" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_ignore_webhook_timestamp_tolerance = true }, DataMasking = ProdMasking() },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown switch")
    };

    private static GatewayOptions WithWarnSwitch(string name) => name switch
    {
        "warn_allow_all_cors_origins" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_all_cors_origins = true }, DataMasking = ProdMasking() },
        "warn_relaxed_query_limits" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_relaxed_query_limits = true }, DataMasking = ProdMasking() },
        "warn_enable_introspection" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_enable_introspection = true }, DataMasking = ProdMasking() },
        "warn_fallback_default_tenant_for_webhooks" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_fallback_default_tenant_for_webhooks = true }, DataMasking = ProdMasking() },
        "catalog_legacy_payload_only_signature" => new GatewayOptions { Catalog = new DataCatalogOptions { AllowLegacyPayloadOnlySignature = true }, DataMasking = ProdMasking() },
        "itsm_legacy_global_webhook_secret" => new GatewayOptions { Itsm = new ItsmOptions { LegacyGlobalWebhookSecret = true }, DataMasking = ProdMasking() },
        "allow_development_in_container" => new GatewayOptions { AllowDevelopmentInContainer = true, DataMasking = ProdMasking() },
        "warn_allow_websql_dml" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_websql_dml = true }, DataMasking = ProdMasking() },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown switch")
    };

    [Theory]
    [InlineData("warn_allow_unmasked_ai_access")]
    [InlineData("warn_mock_external_systems_if_unreachable")]
    [InlineData("warn_auto_approve_access_requests")]
    [InlineData("warn_disable_rate_limiting")]
    [InlineData("warn_allow_unsigned_s3_requests")]
    [InlineData("warn_ignore_webhook_timestamp_tolerance")]
    public void SEM_ReclassifiedSwitches_AreDanger(string name)
    {
        var options = WithNewDangerSwitch(name);

        options.GetActiveDangerBypasses().ShouldContain("DANGER:" + name);
        options.GetActiveWarnings().ShouldBeEmpty();
        options.HasAnyDangerBypassActive.ShouldBeTrue();
        options.HasAnySecurityBypassActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("warn_allow_unmasked_ai_access")]
    [InlineData("warn_mock_external_systems_if_unreachable")]
    [InlineData("warn_auto_approve_access_requests")]
    [InlineData("warn_disable_rate_limiting")]
    [InlineData("warn_allow_unsigned_s3_requests")]
    [InlineData("warn_ignore_webhook_timestamp_tolerance")]
    public void SEM_DangerSwitch_AbortsProductionStartup(string name)
    {
        var options = WithNewDangerSwitch(name);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        ex.Message.ShouldContain("DANGER:" + name);
    }

    [Theory]
    [InlineData("warn_allow_unmasked_ai_access")]
    [InlineData("warn_disable_rate_limiting")]
    public void SEM_DangerSwitch_IsAllowedInDevelopment(string name)
    {
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(WithNewDangerSwitch(name), Env(Environments.Development), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_ExistingDangerSwitches_RemainDanger()
    {
        new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } }
            .GetActiveDangerBypasses().ShouldContain("DANGER:danger_bypass_consent_checks");
        new GatewayOptions { OpenSchema = true }
            .GetActiveDangerBypasses().ShouldContain(b => b.StartsWith("DANGER:open_schema", StringComparison.Ordinal));
        new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_websql_governance = true } }
            .GetActiveDangerBypasses().ShouldContain("DANGER:danger_bypass_websql_governance");
    }

    [Theory]
    [InlineData("warn_relaxed_query_limits")]
    [InlineData("catalog_legacy_payload_only_signature")]
    [InlineData("allow_development_in_container")]
    [InlineData("warn_allow_websql_dml")]
    public void SEM_WarnSwitches_AreWarn_AndStartInProduction(string name)
    {
        var options = WithWarnSwitch(name);

        options.GetActiveWarnings().ShouldContain(w => w.StartsWith("WARN:" + name, StringComparison.Ordinal));
        options.GetActiveDangerBypasses().ShouldBeEmpty();
        options.HasAnyWarningActive.ShouldBeTrue();
        options.HasAnyDangerBypassActive.ShouldBeFalse();
        options.HasAnySecurityBypassActive.ShouldBeTrue();

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_WarnEnableIntrospection_InProduction_RequiresExplicitOptIn()
    {
        var options = WithWarnSwitch("warn_enable_introspection");
        options.GetActiveWarnings().ShouldContain(w => w.StartsWith("WARN:warn_enable_introspection", StringComparison.Ordinal));
        options.GetActiveDangerBypasses().ShouldBeEmpty();

        // RR-L3-05: Prohibited in production without explicit opt-in
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        // Allowed with explicit opt-in
        var optInOptions = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_enable_introspection = true },
            AllowInsecureWarnFlagsInProduction = true,
            DataMasking = ProdMasking()
        };
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(optInOptions, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_WarnAllowAllCorsOrigins_InProduction_RequiresExplicitOptIn()
    {
        var options = WithWarnSwitch("warn_allow_all_cors_origins");
        options.GetActiveWarnings().ShouldContain(w => w.StartsWith("WARN:warn_allow_all_cors_origins", StringComparison.Ordinal));
        options.GetActiveDangerBypasses().ShouldBeEmpty();

        // API-16: Prohibited in production without explicit opt-in
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        // Allowed with explicit opt-in
        var optInOptions = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_all_cors_origins = true },
            AllowInsecureWarnFlagsInProduction = true,
            DataMasking = ProdMasking()
        };
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(optInOptions, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Theory]
    [InlineData("warn_fallback_default_tenant_for_webhooks")]
    [InlineData("itsm_legacy_global_webhook_secret")]
    public void SEM_WebhookTenantFallbackAndLegacySecret_AreDanger_AndBlockedInProduction(string name)
    {
        var options = WithWarnSwitch(name);

        options.GetActiveDangerBypasses().ShouldContain(b => b.StartsWith("DANGER:" + name, StringComparison.Ordinal));
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_AutoCreateConsents_IsDanger_AndBlockedInProduction()
    {
        var options = new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { AutoCreateConsents = true },
            DataMasking = ProdMasking()
        };

        options.GetActiveDangerBypasses().ShouldHaveSingleItem().ShouldBe("DANGER:openmetadata_auto_create_consents (OpenMetadata.AutoCreateConsents)");
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_DbtWebhookSignatureBypass_IsDanger_AndBlockedInProduction()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_webhook_signature_validation = true },
            DataMasking = ProdMasking()
        };

        options.IsWebhookSignatureBypassed.ShouldBeTrue();
        options.GetActiveDangerBypasses().ShouldContain(b => b.StartsWith("DANGER:danger_bypass_webhook_signature_validation", StringComparison.Ordinal));
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_AllowDml_IsRegularOption_WithoutAnyMessage()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = true, DmlWriterRoles = ["WebSqlWriter"] },
            DataMasking = ProdMasking()
        };

        options.GetAllActiveBypasses().ShouldBeEmpty();
        options.HasAnySecurityBypassActive.ShouldBeFalse();
        HealthEndpoints.GetSecurityMode(options).ShouldBe("STRICT_ZERO_TRUST");
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_AllowDml_WithoutWriterRoles_AbortsStartup_InEveryEnvironment()
    {
        var options = new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } };

        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_LegacyDmlAlias_IsWarn_WithHintToAllowDml()
    {
        var insecureAlias = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_websql_dml = true } };

        insecureAlias.IsWebSqlDmlAllowed.ShouldBeTrue();
        insecureAlias.GetActiveWarnings().ShouldContain("WARN:warn_allow_websql_dml (legacy alias, use WebSql.AllowDml)");
        insecureAlias.HasAnyDangerBypassActive.ShouldBeFalse();
    }

    // =========================================================================
    // SEM: health
    // =========================================================================

    private static Task<GatewayHealthReport> CheckHealthAsync(GatewayOptions options, string environment)
    {
        var service = new GatewayHealthCheckService(
            Options.Create(options),
            NullLogger<GatewayHealthCheckService>.Instance,
            governanceRepository: null,
            redisMultiplexer: null,
            environment: Env(environment));
        return service.CheckHealthAsync();
    }

    private static HealthCheckComponentResult SecurityComponent(GatewayHealthReport report) =>
        report.Components.Single(c => c.Name == "SecurityConfiguration");

    [Fact]
    public async Task SEM_Health_DangerMakesSecurityComponentUnhealthy_AndReportUnhealthyOutsideDevelopment()
    {
        var options = WithNewDangerSwitch("warn_disable_rate_limiting");

        var prodReport = await CheckHealthAsync(options, Environments.Production);
        SecurityComponent(prodReport).IsHealthy.ShouldBeFalse();
        prodReport.IsHealthy.ShouldBeFalse();

        // Development keeps /health/ready usable for the getting-started setup; the component still reports the bypass.
        var devReport = await CheckHealthAsync(options, Environments.Development);
        SecurityComponent(devReport).IsHealthy.ShouldBeFalse();
        devReport.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task SEM_Health_WarnKeepsSecurityComponentHealthy_WithDegradedDescription()
    {
        var options = WithWarnSwitch("catalog_legacy_payload_only_signature");

        var report = await CheckHealthAsync(options, Environments.Production);

        report.IsHealthy.ShouldBeTrue();
        var component = SecurityComponent(report);
        component.IsHealthy.ShouldBeTrue();
        component.Description.ShouldNotBeNull();
        component.Description!.ShouldStartWith("degraded: ");
        component.Description!.ShouldContain("WARN:catalog_legacy_payload_only_signature");
    }

    [Fact]
    public async Task SEM_Health_NoSwitches_IsHealthyZeroTrust()
    {
        var report = await CheckHealthAsync(new GatewayOptions(), Environments.Production);

        report.IsHealthy.ShouldBeTrue();
        SecurityComponent(report).IsHealthy.ShouldBeTrue();
        SecurityComponent(report).Description.ShouldNotBeNull();
        SecurityComponent(report).Description!.ShouldNotContain("degraded");
    }

    [Fact]
    public void SEM_SecurityMode_DistinguishesDangerWarnAndStrict()
    {
        HealthEndpoints.GetSecurityMode(new GatewayOptions()).ShouldBe("STRICT_ZERO_TRUST");
        HealthEndpoints.GetSecurityMode(WithWarnSwitch("warn_enable_introspection")).ShouldBe("STRICT_WITH_WARNINGS");
        HealthEndpoints.GetSecurityMode(WithNewDangerSwitch("warn_auto_approve_access_requests")).ShouldBe("INSECURE_DEV_MODE");

        var both = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_enable_introspection = true, danger_bypass_consent_checks = true }
        };
        HealthEndpoints.GetSecurityMode(both).ShouldBe("INSECURE_DEV_MODE");
    }

    // =========================================================================
    // DML guardrails (GovernedSqlExecutionService against SQLite in-memory)
    // =========================================================================

    private const string Tenant = "tenant_a";
    private const string WriterRole = "WebSqlWriter";

    private sealed class DmlFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAlive;

        private DmlFixture(SqliteConnection keepAlive, string connectionString)
        {
            _keepAlive = keepAlive;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public List<AuditLogEntry> AuditEntries { get; } = [];

        public static async Task<DmlFixture> CreateAsync()
        {
            var connectionString = $"Data Source=dml_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            await using (var setup = keepAlive.CreateCommand())
            {
                setup.CommandText =
                    "CREATE TABLE orders (id INTEGER, amount REAL, tenant_id TEXT);" +
                    "INSERT INTO orders VALUES (1, 10, 'tenant_a'), (2, 20, 'tenant_a'), (3, 30, 'tenant_a'), (4, 40, 'tenant_a'), (5, 50, 'tenant_a');" +
                    "INSERT INTO orders VALUES (6, 60, 'tenant_b'), (7, 70, 'tenant_b');";
                await setup.ExecuteNonQueryAsync();
            }

            return new DmlFixture(keepAlive, connectionString);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var cmd = _keepAlive.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await _keepAlive.DisposeAsync();
        }
    }

    private static async Task<DbConnection> OpenSqliteAsync(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static ClaimsPrincipal CreateWriter() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-DML-WRITER"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, WriterRole)
            ],
            "Test"));

    private static GovernedSqlExecutionService CreateDmlService(DmlFixture fixture, bool allowDml = true, long maxAffectedRows = 1000)
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowDml = allowDml,
                DmlWriterRoles = [WriterRole],
                MaxAffectedRows = maxAffectedRows,
                DefaultMaxRows = 100,
                MaxAllowedRows = 500
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = fixture.ConnectionString }
                }
            }
        };

        var orders = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceName = string.Empty, SourceType = "Sqlite" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().Equals(orders.Identifier) ? orders : null));

        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(),
                null,
                hasUnconstrainedColumnAllow: true));

        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => fixture.AuditEntries.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var connectionFactory = Substitute.For<ISqlConnectionFactory>();
        connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenSqliteAsync(fixture.ConnectionString));

        return new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: repo,
            auditLogRepository: audit,
            connectionFactory: connectionFactory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null);
    }

    private static Task ExecuteAsync(GovernedSqlExecutionService service, string sql) =>
        service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest(sql),
            CreateWriter(),
            new TenantId(Tenant),
            (_, _) => Task.CompletedTask);

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("UPDATE orders SET amount = 0")]
    [InlineData("DELETE FROM orders WHERE 1=1")]
    [InlineData("UPDATE orders SET amount = 0 WHERE true")]
    [InlineData("DELETE FROM orders WHERE id = 1 OR 1 = 1")]
    public async Task DML_UnfilteredUpdateOrDelete_IsRejected_AndAudited(string sql)
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, sql));

        ex.Message.ShouldContain("WHERE");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);

        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_REJECTED");
        entry.Decision.ShouldBe("DENY");
        entry.TargetTable.ShouldBe("orders");
        entry.ActorSid.Value.ShouldBe("S-1-5-21-DML-WRITER");
        entry.TenantId.Value.ShouldBe(Tenant);
    }

    [Fact]
    public async Task DML_DeleteWithRealWhere_IsExecuted_AndAuditedWithoutSqlText()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture);

        await ExecuteAsync(service, "DELETE FROM orders WHERE amount = 10");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(6);

        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_EXECUTED");
        entry.Decision.ShouldBe("ALLOW");
        entry.TargetTable.ShouldBe("orders");
        entry.DetailsJson.ShouldContain("\"affectedRows\":1");
        entry.DetailsJson.ShouldContain("\"statementType\":\"Delete\"");
        entry.DetailsJson.ShouldContain("sqlSha256");
        entry.DetailsJson.ShouldNotContain("DELETE FROM");
        entry.DetailsJson.ShouldNotContain("amount = 10");
    }

    [Fact]
    public async Task DML_UpdateIsTenantScoped_AndDoesNotTouchForeignRows()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture);

        await ExecuteAsync(service, "UPDATE orders SET amount = 0 WHERE id > 0");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_a'")).ShouldBe(5);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_b'")).ShouldBe(0);
        fixture.AuditEntries.ShouldHaveSingleItem().DetailsJson.ShouldContain("\"affectedRows\":5");
    }

    [Fact]
    public async Task DML_MaxAffectedRowsExceeded_RollsBack_AndIsAudited()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture, maxAffectedRows: 2);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "UPDATE orders SET amount = 0 WHERE id > 0"));

        ex.Message.ShouldContain("MaxAffectedRows");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);

        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_REJECTED");
        entry.Decision.ShouldBe("DENY");
        entry.DetailsJson.ShouldContain("\"affectedRows\":5");
    }

    [Fact]
    public async Task DML_MaxAffectedRowsWithinLimit_IsCommitted()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture, maxAffectedRows: 2);

        await ExecuteAsync(service, "DELETE FROM orders WHERE id <= 2");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(5);
        fixture.AuditEntries.ShouldHaveSingleItem().EventType.ShouldBe("WEBSQL_DML_EXECUTED");
    }

    [Fact]
    public async Task DML_MaxAffectedRowsZero_IsUnlimited()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture, maxAffectedRows: 0);

        await ExecuteAsync(service, "UPDATE orders SET amount = 0 WHERE id > 0");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(5);
    }

    [Fact]
    public async Task DML_WhenDmlDisabled_RejectionIsAudited()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture, allowDml: false);

        await Should.ThrowAsync<WebSqlPolicyException>(() => ExecuteAsync(service, "DELETE FROM orders WHERE id = 1"));

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_REJECTED");
        entry.DetailsJson.ShouldNotContain("DELETE FROM");
    }

    [Fact]
    public void DML_MaxAffectedRows_DefaultIs1000()
    {
        new WebSqlOptions().MaxAffectedRows.ShouldBe(1000);
    }
}
