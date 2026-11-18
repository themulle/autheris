namespace Autheris.Tests.Unit.Policy;

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
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Health;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Autheris.Application.Sql.Errors;
using Xunit;

/// <summary>
/// SEM_*: DANGER/WARN semantics of the security switches (DANGER blocks outside Development and is unhealthy,
/// WARN is permitted and reported, regular options are silent).
/// DML_*: WebSQL DML guardrails (unfiltered UPDATE/DELETE rejected, WebSql.MaxAffectedRows with rollback, DML audit).
/// </summary>
[Trait("Category", "Security")]
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
        "warn_allow_all_cors_origins" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_allow_all_cors_origins = true }, DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" } },
        "warn_relaxed_query_limits" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_relaxed_query_limits = true }, DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" } },
        "warn_enable_introspection" => new GatewayOptions { Insecure = new InsecureGettingStartedOptions { warn_enable_introspection = true }, DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" } },
        "allow_development_in_container" => new GatewayOptions { AllowDevelopmentInContainer = true, DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" } },
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
        new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_websql_governance = true } }
            .GetActiveDangerBypasses().ShouldContain("DANGER:danger_bypass_websql_governance");
    }

    [Theory]
    [InlineData("warn_relaxed_query_limits")]
    [InlineData("allow_development_in_container")]
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
            DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(optInOptions, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_EnableIntrospection_InProduction_RequiresExplicitOptIn()
    {
        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { EnableIntrospection = true },
            DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };

        // Prohibited in production without explicit opt-in
        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        // Allowed with explicit opt-in
        var optInOptions = new GatewayOptions
        {
            GraphQL = new GraphQLOptions { EnableIntrospection = true },
            AllowInsecureWarnFlagsInProduction = true,
            DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
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
            DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(optInOptions, Env(Environments.Production), NoEnvironmentVariables));
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
            WebSql = new WebSqlOptions { AllowDml = true },
            DataMasking = ProdMasking(), Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };

        options.GetAllActiveBypasses().ShouldBeEmpty();
        options.HasAnySecurityBypassActive.ShouldBeFalse();
        HealthEndpoints.GetSecurityMode(options).ShouldBe("STRICT_ZERO_TRUST");
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void SEM_AllowDml_WithoutRoles_SucceedsInEveryEnvironment()
    {
        var options = new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), NoEnvironmentVariables));
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
    public async Task SEM_Health_DangerDescription_DoesNotListTheActiveBypasses()
    {
        var options = WithNewDangerSwitch("warn_disable_rate_limiting");

        var report = await CheckHealthAsync(options, Environments.Production);

        var description = SecurityComponent(report).Description;
        description.ShouldNotBeNull();
        description!.ShouldStartWith("WARNING: ");
        description.ShouldNotContain("warn_disable_rate_limiting");
        description.ShouldNotContain("DANGER:");
    }

    [Fact]
    public async Task SEM_Health_WarnKeepsSecurityComponentHealthy_WithDegradedDescription()
    {
        var options = WithWarnSwitch("warn_relaxed_query_limits");

        var report = await CheckHealthAsync(options, Environments.Production);

        report.IsHealthy.ShouldBeTrue();
        var component = SecurityComponent(report);
        component.IsHealthy.ShouldBeTrue();
        component.Description.ShouldNotBeNull();
        component.Description!.ShouldStartWith("degraded: ");
        // The names of active switches are logged, never returned in the health details.
        component.Description!.ShouldNotContain("warn_relaxed_query_limits");
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

    internal const string Tenant = "tenant_a";
    internal const string WriterRole = "WebSqlWriter";

    internal sealed class DmlFixture : IAsyncDisposable
    {
        public string ConnectionString => "Host=unused;Database=dml";

        public List<AuditLogEntry> AuditEntries { get; } = [];

        public List<string> ExecutedSql { get; } = [];

        public static Task<DmlFixture> CreateAsync() => Task.FromResult(new DmlFixture());

        public Task<long> ScalarAsync(string sql)
        {
            ExecutedSql.ShouldBeEmpty();
            return Task.FromResult(sql.Contains("amount = 0", StringComparison.OrdinalIgnoreCase) ? 0L : 7L);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<DbConnection> OpenCallbackConnectionAsync(DmlFixture fixture)
    {
        var connection = new CallbackDbConnection((sql, _) =>
        {
            fixture.ExecutedSql.Add(sql);
            return new StubDbDataReader([], []);
        });
        await connection.OpenAsync();
        return connection;
    }

    internal static ClaimsPrincipal CreateWriter(bool withWriteScope = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-DML-WRITER"),
            new("tenant_id", Tenant),
            new(ClaimTypes.Role, WriterRole)
        };
        if (withWriteScope)
        {
            claims.Add(new Claim("scp", "Sql.Write"));
        }

        return Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
    }

    internal static GovernedSqlExecutionService CreateDmlService(DmlFixture fixture, bool allowDml = true, long maxAffectedRows = 1000, bool permissiveWrite = true, bool withDependency = false, bool columnlessHolder = false, bool plainTable = false, bool requireTenantColumn = false, string[]? exemptTables = null)
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                AllowDml = allowDml,
                MaxAffectedRows = maxAffectedRows,
                DefaultMaxRows = 100,
                MaxAllowedRows = 500
            },
            DataSources = new SqlDataSourceOptions
            {
                RequireTenantColumn = requireTenantColumn,
                TenantColumnExemptTables = (exemptTables ?? []).ToList(),
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = fixture.ConnectionString, WriteConnectionString = fixture.ConnectionString, DefaultSchema = "public" }
                }
            }
        };

        var orders = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        // withDependency: `orders` carries a row policy that reads `public.consents`, which is then a policy dependency table.
        var consents = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "consents"),
            Table = new Table { TableName = "consents", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }]
        };
        // columnlessHolder: an active catalog table without columns whose row policy reads `public.consents`; plainTable: a table without a tenant column.
        var holder = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "holder"),
            Table = new Table { TableName = "holder", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql" },
            Columns = []
        };
        var plain = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "plain"),
            Table = new Table { TableName = "plain", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var known = withDependency || columnlessHolder ? new[] { orders, consents } : plainTable ? new[] { orders, plain } : new[] { orders };
        TableMetadata[] catalog = columnlessHolder ? [.. known, holder] : known;
        var repo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(known.FirstOrDefault(t => t.Identifier.Equals(ci.Arg<TableIdentifier>()))));
        repo.StubAllTables(Task.FromResult<IReadOnlyList<TableMetadata>>(catalog));

        var writeConsents = new List<Consent>();
        if (permissiveWrite)
        {
            writeConsents.Add(new Consent
            {
                TableIdentifier = orders.Identifier,
                TenantId = new TenantId(Tenant),
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-1-5-21-DML-WRITER"),
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "amount", "tenant_id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "amount", "tenant_id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
                ]
            });
            writeConsents.Add(new Consent
            {
                TableIdentifier = plain.Identifier,
                TenantId = new TenantId(Tenant),
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-1-5-21-DML-WRITER"),
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
                ]
            });
        }

        // Reads stay independent of writes: every table is also readable by the writer through a plain read consent.
        foreach (var readable in new[] { orders, consents, holder, plain })
        {
            writeConsents.Add(new Consent
            {
                TableIdentifier = readable.Identifier,
                TenantId = new TenantId(Tenant),
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-1-5-21-DML-WRITER"),
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1)
            });
        }

        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(writeConsents.Where(c => c.TableIdentifier.Equals(ci.ArgAt<TableIdentifier>(1))).ToList()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci =>
            {
                var allowed = TableAccessDecision.Allowed(
                    ci.ArgAt<TableIdentifier>(3),
                    new Dictionary<string, ColumnAccessLevel>(),
                    null,
                    hasUnconstrainedColumnAllow: true);
                return (columnlessHolder ? ci.ArgAt<TableIdentifier>(3).Equals(holder.Identifier) : withDependency && ci.ArgAt<TableIdentifier>(3).Equals(orders.Identifier))
                    ? allowed with { RowPolicy = Autheris.Tests.Unit.GraphQL.SecuredTreeHarness.ExistsPolicy(schema: "public") }
                    : allowed;
            });

        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e =>
        {
            // The write-ahead intent record is covered by DmlWriteAheadAuditTests; these tests assert the outcome record.
            if (e.EventType != Autheris.Domain.Audit.AuditEventTypes.WebSqlDmlIntent)
            {
                fixture.AuditEntries.Add(e);
            }
        }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var connectionFactory = Substitute.For<ISqlConnectionFactory>();
        connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenCallbackConnectionAsync(fixture));

        var pdp = new Autheris.Application.Policy.Pdp.AccessDecisionPoint(
            Options.Create(options),
            consentResolution,
            consentRepository,
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            writeGrantEvaluator: new Autheris.Application.Policy.WriteGrants.WriteGrantEvaluator(consentRepository, allowDeleteGrants: permissiveWrite));

        return global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), executionGuard: GovernedTestSupport.Guard,
            consentResolution: consentResolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            auditLogRepository: audit,
            connectionFactory: connectionFactory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null,
            sqlEngine: new TrinoSqlEngine.FastSqlEngine(),
            accessDecisionPoint: pdp);
    }

    internal static Task ExecuteAsync(GovernedSqlExecutionService service, string sql) =>
        service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql),
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

        // X1: the engine's typed rejection (stable code SQL_UNFILTERED_DML) replaces the former WebSqlPolicyException text.
        var ex = await Should.ThrowAsync<UnfilteredDmlException>(() => ExecuteAsync(service, sql));

        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Code.ShouldBe("SQL_UNFILTERED_DML");
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
    public async Task DML_ReadOnlyTokenWithWriterRole_IsRejected()
    {
        await using var fixture = await DmlFixture.CreateAsync();
        var service = CreateDmlService(fixture);
        var writer = CreateWriter();
        TokenAccessScope.MarkReadOnly((ClaimsIdentity)writer.Identity!);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest("DELETE FROM orders WHERE amount = 10", Transport: SqlTransports.WebSql),
            writer,
            new TenantId(Tenant),
            (_, _) => Task.CompletedTask));

        ex.Message.ShouldContain("read access");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
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
