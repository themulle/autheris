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
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Infrastructure.Connectors;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
[Trait("Category", "Security")]
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
    // GAP03: Webhook timestamp header parser (Unix seconds or ISO 8601)
    // =========================================================================

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
        // WebSql.AllowDml is allowed in Production without role configuration (authorization uses write consent grants).
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = true },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }, Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void GAP04_ContainerOptIn_IsReported()
    {
        var options = new GatewayOptions
        {
            AllowDevelopmentInContainer = true
        };

        options.HasAnySecurityBypassActive.ShouldBeTrue();
        var bypasses = options.GetAllActiveBypasses();
        bypasses.ShouldContain(b => b.StartsWith("WARN:allow_development_in_container", StringComparison.Ordinal));
    }

    [Fact]
    public void GAP04_WebSqlGovernanceBypass_IsDanger_AndDml_IsNotReported()
    {
        var governanceBypass = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_websql_governance = true } };
        governanceBypass.HasAnyDangerBypassActive.ShouldBeTrue();
        governanceBypass.GetAllActiveBypasses().ShouldContain("DANGER:danger_bypass_websql_governance");

        var dml = new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true } };
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
    // GAP05: DefaultEnvironmentSecretProvider
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

    [Fact]
    public void GAP05_OtherReferences_InDevelopment_KeepPlaceholderBehaviour()
    {
        var provider = CreateSecretProvider("Development");

        Encoding.UTF8.GetString(provider.GetSecretBytes("gap-a-unconfigured-dev-key")).ShouldBe("gap-a-unconfigured-dev-key");
    }

    // =========================================================================
    // GAP08 / ARC-I-13: SqlConnector requires a masking provider (DB read covered in IntegrationGapASqlConnectorIntegrationTests)
    // =========================================================================

    private const string ConnectorId = "gap-a-sql";

    [Fact]
    public void GAP08_SqlConnector_WithoutMaskingProvider_CannotBeConstructed_FailClosed()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        Should.Throw<ArgumentNullException>(() => new SqlConnector(
            connectorId: ConnectorId,
            metadataRepository: Substitute.For<ITableMetadataRepository>(),
            maskingProvider: null!,
            scopeFactory: scopeFactory));
    }
}
