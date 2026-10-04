namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Infrastructure.Connectors;
using Autheris.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Integration gaps, round 3, work package GAP-A (GAP03 catalog webhook timestamp flow, GAP04 bypass list,
/// GAP05 instance-specific ITSM secrets, GAP08 SqlConnector HMAC pseudonymization).
/// GAP01/GAP02 (endpoint metadata) are covered in Autheris.Tests.Integration/IntegrationGapAEndpointMetadataTests.cs.
/// </summary>
public sealed class IntegrationGapATests
{
    private static readonly Func<string, string?> NoEnvironmentVariables = _ => null;

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    // =========================================================================
    // GAP03: Catalog webhook – header (Unix seconds or ISO 8601) -> endpoint parser -> handler signs "{unixSeconds}.{payload}"
    // =========================================================================

    private const string CatalogSecret = "gap-a-catalog-webhook-secret";

    private static string CatalogPayload() =>
        "{\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"sales_dw.public.orders\"}";

    private static string SignCatalog(long unixSeconds, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(CatalogSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + "." + payload));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    private static CatalogWebhookHandler CreateCatalogHandler() =>
        new(
            Options.Create(new GatewayOptions { Catalog = new DataCatalogOptions { WebhookSecret = CatalogSecret } }),
            Substitute.For<IPolicyEpochRepository>(),
            Substitute.For<IDataCatalogSyncService>(),
            NullLogger<CatalogWebhookHandler>.Instance);

    /// <summary>Exactly the endpoint data flow: parse the timestamp header, then hand the parsed value to the handler.</summary>
    private static async Task<CatalogWebhookResult> DeliverAsync(string payload, string signature, string timestampHeader)
    {
        EndpointSecurity.TryParseWebhookTimestamp(timestampHeader, out var parsed).ShouldBeTrue();
        return await CreateCatalogHandler().HandleWebhookAsync(payload, signature, parsed, null, CancellationToken.None);
    }

    [Fact]
    public async Task GAP03_ValidSignature_WithUnixSecondsHeader_IsAccepted()
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = CatalogPayload();

        var result = await DeliverAsync(payload, SignCatalog(unix, payload), unix.ToString(CultureInfo.InvariantCulture));

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe("INVALIDATED");
    }

    [Fact]
    public async Task GAP03_ValidSignature_WithIso8601Header_IsAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = CatalogPayload();

        // Round-trip ISO 8601 with sub-second precision: the signed Unix second is the truncated value of the same instant.
        var result = await DeliverAsync(payload, SignCatalog(now.ToUnixTimeSeconds(), payload), now.ToString("O", CultureInfo.InvariantCulture));

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe("INVALIDATED");
    }

    [Fact]
    public async Task GAP03_ValidSignature_WithIso8601HeaderAndOffset_IsAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = CatalogPayload();
        var isoWithOffset = now.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        var result = await DeliverAsync(payload, SignCatalog(now.ToUnixTimeSeconds(), payload), isoWithOffset);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task GAP03_ManipulatedUnixTimestamp_IsRejected()
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = CatalogPayload();
        var signature = SignCatalog(unix, payload);

        // Replay with a fresh (still in-tolerance) timestamp but the old signature.
        var result = await DeliverAsync(payload, signature, (unix + 60).ToString(CultureInfo.InvariantCulture));

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("REJECTED_INVALID_SIGNATURE");
    }

    [Fact]
    public async Task GAP03_ManipulatedIsoTimestamp_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = CatalogPayload();
        var signature = SignCatalog(now.ToUnixTimeSeconds(), payload);

        var result = await DeliverAsync(payload, signature, now.AddSeconds(30).ToString("O", CultureInfo.InvariantCulture));

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("REJECTED_INVALID_SIGNATURE");
    }

    [Fact]
    public void GAP03_UnixAndIsoHeaders_OfSameInstant_YieldSameSignedSecond()
    {
        var now = DateTimeOffset.UtcNow;

        EndpointSecurity.TryParseWebhookTimestamp(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), out var fromUnix).ShouldBeTrue();
        EndpointSecurity.TryParseWebhookTimestamp(now.ToString("O", CultureInfo.InvariantCulture), out var fromIso).ShouldBeTrue();

        fromUnix.ToUnixTimeSeconds().ShouldBe(now.ToUnixTimeSeconds());
        fromIso.ToUnixTimeSeconds().ShouldBe(now.ToUnixTimeSeconds());
    }

    // =========================================================================
    // GAP04: Insecure opt-ins are reported (DANGER/WARN semantics, see BypassSemanticsAndDmlGuardrailTests)
    // =========================================================================

    [Fact]
    public void GAP04_OpenMetadataAutoCreateConsents_IsReportedAsDanger()
    {
        var options = new GatewayOptions { OpenMetadata = new OpenMetadataOptions { AutoCreateConsents = true } };

        options.HasAnySecurityBypassActive.ShouldBeTrue();
        options.HasAnyDangerBypassActive.ShouldBeTrue();
        options.GetActiveDangerBypasses().ShouldContain(o => o.StartsWith("DANGER:openmetadata_auto_create_consents", StringComparison.Ordinal));
    }

    [Fact]
    public void GAP04_ProductionWarnings_AndRegularDml_DoNotAbortStartup()
    {
        // WARN entries (e.g. Itsm.LegacyGlobalWebhookSecret) and the regular option WebSql.AllowDml + DmlWriterRoles are allowed in Production.
        var options = new GatewayOptions
        {
            Itsm = new ItsmOptions { LegacyGlobalWebhookSecret = true },
            WebSql = new WebSqlOptions { AllowDml = true, DmlWriterRoles = ["WebSqlWriter"] },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void GAP04_WebSqlDml_WithoutWriterRoles_IsRejected()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = true },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        ex.Message.ShouldContain("DmlWriterRoles");
    }

    [Fact]
    public void GAP04_LegacyWarnDmlSwitch_IsWarn_AndStartsInProduction()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { warn_allow_dml = true, DmlWriterRoles = ["WebSqlWriter"] },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }
        };

        options.GetActiveWarnings().ShouldContain(w => w.StartsWith("WARN:warn_allow_websql_dml", StringComparison.Ordinal) &&
                                                       w.Contains("use WebSql.AllowDml", StringComparison.Ordinal));
        options.HasAnyDangerBypassActive.ShouldBeFalse();

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void GAP04_LegacyAndContainerOptIns_AreReported()
    {
        var options = new GatewayOptions
        {
            Itsm = new ItsmOptions { LegacyGlobalWebhookSecret = true },
            Catalog = new DataCatalogOptions { AllowLegacyPayloadOnlySignature = true },
            AllowDevelopmentInContainer = true
        };

        options.HasAnySecurityBypassActive.ShouldBeTrue();
        options.HasAnyDangerBypassActive.ShouldBeFalse();
        var bypasses = options.GetAllActiveBypasses();
        bypasses.ShouldContain(b => b.StartsWith("WARN:itsm_legacy_global_webhook_secret", StringComparison.Ordinal));
        bypasses.ShouldContain(b => b.StartsWith("WARN:catalog_legacy_payload_only_signature", StringComparison.Ordinal));
        bypasses.ShouldContain(b => b.StartsWith("WARN:allow_development_in_container", StringComparison.Ordinal));
    }

    [Fact]
    public void GAP04_WebSqlGovernanceBypass_IsDanger_AndDml_IsNotReported()
    {
        var governanceBypass = new GatewayOptions { WebSql = new WebSqlOptions { danger_bypass_sql_governance = true } };
        governanceBypass.HasAnyDangerBypassActive.ShouldBeTrue();
        governanceBypass.GetAllActiveBypasses().ShouldContain("DANGER:danger_bypass_websql_governance");

        var dml = new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true, DmlWriterRoles = ["WebSqlWriter"] } };
        dml.HasAnySecurityBypassActive.ShouldBeFalse();
        dml.GetAllActiveBypasses().ShouldBeEmpty();
    }

    [Fact]
    public void GAP04_DefaultOptions_HaveNoBypass()
    {
        var options = new GatewayOptions();

        options.HasAnySecurityBypassActive.ShouldBeFalse();
        options.GetAllActiveBypasses().ShouldBeEmpty();
        options.GetActiveWarnings().ShouldBeEmpty();
        options.GetActiveDangerBypasses().ShouldBeEmpty();
    }

    // =========================================================================
    // GAP05: DefaultEnvironmentSecretProvider – instance-specific ITSM secrets
    // =========================================================================

    private static DefaultEnvironmentSecretProvider CreateSecretProvider(string environment, params (string Key, string Value)[] entries)
    {
        var values = new List<KeyValuePair<string, string?>>();
        foreach (var (key, value) in entries)
        {
            values.Add(new KeyValuePair<string, string?>(key, value));
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new DefaultEnvironmentSecretProvider(configuration, Env(environment));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void GAP05_InstanceSecret_DoesNotFallBackToGlobalSecret(string environment)
    {
        var provider = CreateSecretProvider(environment,
            ("ITSM_WEBHOOK_SECRET", "global-secret"),
            ("ITSM__WEBHOOK_SECRET", "global-secret"),
            ("Gateway:Itsm:WebhookSecret", "global-secret"),
            ("itsm:webhook-secret", "global-secret"));

        Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes("itsm:webhook-secret:gap-a-inst-unknown"));
    }

    [Fact]
    public void GAP05_InstanceSecret_InDevelopment_DoesNotReturnReferenceNameAsSecret()
    {
        var provider = CreateSecretProvider("Development");

        Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes("itsm:webhook-secret:gap-a-inst-dev"));
    }

    [Fact]
    public void GAP05_ConfiguredInstanceSecret_IsResolved()
    {
        var provider = CreateSecretProvider("Production",
            ("itsm:webhook-secret:gap-a-inst-1", "instance-secret-1"),
            ("ITSM_WEBHOOK_SECRET", "global-secret"));

        Encoding.UTF8.GetString(provider.GetSecretBytes("itsm:webhook-secret:gap-a-inst-1")).ShouldBe("instance-secret-1");
    }

    [Fact]
    public void GAP05_GlobalItsmSecret_StillResolvesViaAliases()
    {
        var provider = CreateSecretProvider("Production", ("ITSM_WEBHOOK_SECRET", "global-secret"));

        Encoding.UTF8.GetString(provider.GetSecretBytes("itsm:webhook-secret")).ShouldBe("global-secret");
    }

    [Fact]
    public void GAP05_OtherReferences_InDevelopment_KeepPlaceholderBehaviour()
    {
        var provider = CreateSecretProvider("Development");

        Encoding.UTF8.GetString(provider.GetSecretBytes("gap-a-unconfigured-dev-key")).ShouldBe("gap-a-unconfigured-dev-key");
    }

    // =========================================================================
    // GAP08: SqlConnector pseudonymizes HMAC columns via IColumnMaskingProvider
    // =========================================================================

    private const string ConnectorId = "gap-a-sql";
    private const string RawSsn = "123-45-6789";

    private static TableMetadata CreateEmployeesMetadata() => new()
    {
        Identifier = new TableIdentifier("corp", "hr", "employees"),
        Table = new Table
        {
            SourceName = ConnectorId,
            SchemaName = "hr",
            TableName = "employees",
            DataSourceType = DataSourceType.Sql,
            SourceType = "Sqlite"
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] = new MaskingRule { RuleType = "HMAC_SHA256" }
        }
    };

    private static async Task<DbConnection> OpenSqliteAsync(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadSsnViaConnectorAsync(IColumnMaskingProvider? maskingProvider)
    {
        var connectionString = $"Data Source=gapa08_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var setup = keepAlive.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE employees (id INTEGER, name TEXT, ssn TEXT); INSERT INTO employees VALUES (1, 'Alice', '" + RawSsn + "');";
            await setup.ExecuteNonQueryAsync();
        }

        var connectionFactory = Substitute.For<ISqlConnectionFactory>();
        connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenSqliteAsync(connectionString));

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [ConnectorId] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = connectionString }
                }
            }
        });

        var metadata = CreateEmployeesMetadata();
        var connector = new SqlConnector(
            connectorId: ConnectorId,
            connectionFactory: connectionFactory,
            metadataRepository: Substitute.For<ITableMetadataRepository>(),
            options: options,
            maskingProvider: maskingProvider);

        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        });

        var session = new ConnectorSessionContext(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GAPA")], "Test")),
            Tenant: new TenantId("tenant-gap-a"),
            AccessDecision: decision,
            ProjectedColumns: ["id", "name", "ssn"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = metadata;

        return await connector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), session);
    }

    [Fact]
    public async Task GAP08_SqlConnector_WithMaskingProvider_PseudonymizesHmacColumn()
    {
        var maskingProvider = new ColumnMaskingProvider(Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "gap-a-test-hmac-key" }
        }));

        var first = await ReadSsnViaConnectorAsync(maskingProvider);
        var second = await ReadSsnViaConnectorAsync(maskingProvider);

        first.Count.ShouldBe(1);
        var pseudonym = first[0]["ssn"]?.ToString();
        pseudonym.ShouldNotBeNullOrWhiteSpace();
        pseudonym.ShouldNotBe(RawSsn);
        pseudonym.ShouldNotBe("***");
        // Deterministic pseudonym (joinable), not a redaction.
        second.Count.ShouldBe(1);
        second[0]["ssn"].ShouldNotBeNull();
        second[0]["ssn"]!.ToString().ShouldBe(pseudonym);
        first[0]["name"].ShouldBe("Alice");
    }

    [Fact]
    public async Task GAP08_SqlConnector_WithoutMaskingProvider_RedactsHmacColumn_FailClosed()
    {
        var rows = await ReadSsnViaConnectorAsync(maskingProvider: null);

        rows.Count.ShouldBe(1);
        rows[0]["ssn"].ShouldBe("***");
    }
}
