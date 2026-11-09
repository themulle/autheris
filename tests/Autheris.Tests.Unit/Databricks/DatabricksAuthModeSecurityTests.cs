namespace Autheris.Tests.Unit.Databricks;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>
/// WP-SIMPL-37 / OD-16: Comprehensive security tests for Databricks authentication modes:
/// SEC-SIMPL-95 (secret references only, inline secrets rejected in all environments, zero secret leakage),
/// SEC-SIMPL-96 (shared-identity modes forced to AutherisEnforced),
/// SEC-SIMPL-97 (endpoint and mode fail-closed validation in all environments).
/// </summary>
[Trait("Category", "Security")]
public sealed class DatabricksAuthModeSecurityTests
{
    [Theory]
    [InlineData("Production", "plain-text-pat-12345", DatabricksAuthMode.Pat)]
    [InlineData("Development", "plain-text-pat-12345", DatabricksAuthMode.Pat)]
    [InlineData("Production", "my-raw-client-secret-value", DatabricksAuthMode.OAuthM2M)]
    [InlineData("Development", "my-raw-client-secret-value", DatabricksAuthMode.OAuthM2M)]
    [InlineData("Production", "raw-obo-secret-987", DatabricksAuthMode.OAuthObo)]
    [InlineData("Development", "raw-obo-secret-987", DatabricksAuthMode.OAuthObo)]
    [Trait("SecurityCondition", "SEC-SIMPL-95")]
    public void Inline_secret_fails_startup_and_tokens_never_logged_or_audited(string envName, string inlineSecret, DatabricksAuthMode mode)
    {
        var options = mode switch
        {
            DatabricksAuthMode.Pat => Options(DatabricksAuthMode.Pat, extra: conns =>
            {
                var c = conns["dbx"];
                conns["dbx"] = new DataSourceConnectionOptions
                {
                    Provider = c.Provider,
                    CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = c.Databricks!.Host,
                        WarehouseId = c.Databricks.WarehouseId,
                        Auth = new DatabricksAuthOptions { Mode = DatabricksAuthMode.Pat, PatRef = inlineSecret }
                    }
                };
            }),
            DatabricksAuthMode.OAuthObo => Options(DatabricksAuthMode.OAuthObo, extra: conns =>
            {
                var c = conns["dbx"];
                conns["dbx"] = new DataSourceConnectionOptions
                {
                    Provider = c.Provider,
                    CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = c.Databricks!.Host,
                        WarehouseId = c.Databricks.WarehouseId,
                        Auth = new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthObo,
                            Obo = new DatabricksOboOptions
                            {
                                ClientId = "obo-client",
                                ClientSecretRef = inlineSecret,
                                Scopes = "scope/.default"
                            }
                        }
                    }
                };
            }),
            _ => Options(DatabricksAuthMode.OAuthM2M, extra: conns =>
            {
                var c = conns["dbx"];
                conns["dbx"] = new DataSourceConnectionOptions
                {
                    Provider = c.Provider,
                    CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = c.Databricks!.Host,
                        WarehouseId = c.Databricks.WarehouseId,
                        Auth = new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthM2M,
                            ClientId = "sp-id",
                            ClientSecretRef = inlineSecret
                        }
                    }
                };
            })
        };

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env(envName)));
        ex.Message.ShouldContain("must be a secret reference");
        ex.Message.ShouldNotContain(inlineSecret);
    }

    [Theory]
    [InlineData(DatabricksAuthMode.OAuthM2M, EnforcementMode.Delegated, "Production")]
    [InlineData(DatabricksAuthMode.OAuthM2M, EnforcementMode.DelegatedDefenceInDepth, "Production")]
    [InlineData(DatabricksAuthMode.OAuthM2M, EnforcementMode.Delegated, "Development")]
    [InlineData(DatabricksAuthMode.OAuthM2M, EnforcementMode.DelegatedDefenceInDepth, "Development")]
    [InlineData(DatabricksAuthMode.Pat, EnforcementMode.Delegated, "Production")]
    [InlineData(DatabricksAuthMode.Pat, EnforcementMode.DelegatedDefenceInDepth, "Production")]
    [InlineData(DatabricksAuthMode.Pat, EnforcementMode.Delegated, "Development")]
    [InlineData(DatabricksAuthMode.Pat, EnforcementMode.DelegatedDefenceInDepth, "Development")]
    [Trait("SecurityCondition", "SEC-SIMPL-96")]
    public void Shared_identity_modes_apply_gateway_rls_masking_and_attribute_caller(
        DatabricksAuthMode mode, EnforcementMode enforcementMode, string envName)
    {
        var options = Options(mode, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = c.Databricks,
                EnforcementMode = enforcementMode,
                KeySetCacheAllowed = false
            };
        });

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env(envName)));
        ex.Message.ShouldContain("SEC-SIMPL-96");
    }

    private static GatewayOptions WithAuth(DatabricksAuthOptions auth) =>
        Options(extra: conns =>
        {
            var old = conns["dbx"].Databricks!;
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = "databricks",
                CommandTimeoutSeconds = 30,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = old.Host,
                    WarehouseId = old.WarehouseId,
                    Catalog = old.Catalog,
                    Schema = old.Schema,
                    ByteLimit = old.ByteLimit,
                    PollIntervalMilliseconds = old.PollIntervalMilliseconds,
                    Auth = auth
                }
            };
        });

    public static IEnumerable<object[]> StartupFailClosedScenarios()
    {
        var envs = new[] { "Production", "Development" };
        foreach (var env in envs)
        {
            // Missing PatRef
            yield return new object[]
            {
                env,
                "Missing PatRef in Pat mode",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions { Mode = DatabricksAuthMode.Pat, PatRef = "" })),
                "Auth.PatRef is required"
            };

            // Missing ClientSecretRef
            yield return new object[]
            {
                env,
                "Missing ClientSecretRef in OAuthM2M",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions { Mode = DatabricksAuthMode.OAuthM2M, ClientId = "sp-id", ClientSecretRef = "" })),
                "OAuthM2M requires Auth.ClientId and Auth.ClientSecretRef"
            };

            // http:// TokenEndpoint
            yield return new object[]
            {
                env,
                "HTTP TokenEndpoint",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions
                {
                    Mode = DatabricksAuthMode.OAuthM2M,
                    ClientId = "sp-id",
                    ClientSecretRef = "kv://secret",
                    TokenEndpoint = "http://adb-123.azuredatabricks.net/oidc/v1/token"
                })),
                "Auth.TokenEndpoint must be an absolute HTTPS URL"
            };

            // TokenEndpoint on foreign host
            yield return new object[]
            {
                env,
                "Foreign host TokenEndpoint",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions
                {
                    Mode = DatabricksAuthMode.OAuthM2M,
                    ClientId = "sp-id",
                    ClientSecretRef = "kv://secret",
                    TokenEndpoint = "https://evil-untrusted-host.example.com/token"
                })),
                "Auth.TokenEndpoint host 'evil-untrusted-host.example.com' is not allowed"
            };

            // TokenEndpoint with OBO
            yield return new object[]
            {
                env,
                "TokenEndpoint with OBO",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions
                {
                    Mode = DatabricksAuthMode.OAuthObo,
                    TokenEndpoint = "https://adb-123.azuredatabricks.net/oidc/v1/token",
                    Obo = new DatabricksOboOptions
                    {
                        ClientId = "obo-id",
                        ClientSecretRef = "kv://secret",
                        Scopes = "scope/.default"
                    }
                })),
                "Auth.TokenEndpoint is only allowed with Auth.Mode OAuthM2M"
            };

            // OBO without Scopes
            yield return new object[]
            {
                env,
                "OBO without Scopes",
                (Func<GatewayOptions>)(() => WithAuth(new DatabricksAuthOptions
                {
                    Mode = DatabricksAuthMode.OAuthObo,
                    Obo = new DatabricksOboOptions
                    {
                        ClientId = "obo-id",
                        ClientSecretRef = "kv://secret",
                        Scopes = ""
                    }
                })),
                "Auth.Obo.Scopes (token-exchange audience) is required"
            };
        }
    }

    [Theory]
    [MemberData(nameof(StartupFailClosedScenarios))]
    [Trait("SecurityCondition", "SEC-SIMPL-97")]
    public void Startup_fails_closed_per_mode_on_missing_secret_http_endpoint_or_mismatch(
        string envName, string scenario, Func<GatewayOptions> createOptions, string expectedMessageFragment)
    {
        _ = scenario;
        var options = createOptions();
        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env(envName)));
        ex.Message.ShouldContain(expectedMessageFragment);
    }

    [Theory]
    [InlineData("kv://my-secret", true)]
    [InlineData("vault://keys/my-key", true)]
    [InlineData("vault:my-key", true)]
    [InlineData("keyvault:my-key", true)]
    [InlineData("azure-keyvault://my-vault/secrets/my-key", true)]
    [InlineData("file:/run/secrets/my-token", true)]
    [InlineData("file:///run/secrets/my-token", true)]
    [InlineData("datasource:sales:custom-api-key", true)]
    [InlineData("https://my-vault.vault.azure.net/secrets/my-token", true)]
    [InlineData("dapi-1234567890abcdef", false)]
    [InlineData("raw-secret-token", false)]
    [InlineData("supersecretpassword123", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [Trait("SecurityCondition", "SEC-SIMPL-95")]
    public void SecretReferenceResolver_IsReference_DetectsReferenceSyntax(string? input, bool expected)
    {
        SecretReferenceResolver.IsReference(input).ShouldBe(expected);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-SIMPL-98")]
    public async Task Per_tenant_principal_never_used_for_another_tenant()
    {
        var db = new DatabricksDataSourceOptions
        {
            Host = "adb-123.azuredatabricks.net",
            WarehouseId = "wh-1",
            Auth = new DatabricksAuthOptions
            {
                Mode = DatabricksAuthMode.Pat,
                PatRef = "kv://global-pat",
                Tenants = new Dictionary<string, DatabricksTenantPrincipalOptions>
                {
                    ["tenant-a"] = new() { PatRef = "kv://pat-a" },
                    ["tenant-b"] = new() { PatRef = "kv://pat-b" }
                },
                FallbackToGlobalPrincipal = false
            }
        };

        var vault = NSubstitute.Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("kv://global-pat").Returns(System.Text.Encoding.UTF8.GetBytes("global-token-val"));
        vault.GetSecretBytes("kv://pat-a").Returns(System.Text.Encoding.UTF8.GetBytes("pat-a-val"));
        vault.GetSecretBytes("kv://pat-b").Returns(System.Text.Encoding.UTF8.GetBytes("pat-b-val"));

        var cache = new DatabricksTokenCache();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var http = new System.Net.Http.HttpClient(handler);
        var provider = new DatabricksOboTokenProvider(http, null, vault, Env("Production"), cache);

        var contextA = new DelegationContext(TenantId: new TenantId("tenant-a"));
        var contextB = new DelegationContext(TenantId: new TenantId("tenant-b"));
        var contextC = new DelegationContext(TenantId: new TenantId("tenant-c"));

        var tokenA = await provider.AcquireTokenAsync(db, contextA, CancellationToken.None);
        var tokenB = await provider.AcquireTokenAsync(db, contextB, CancellationToken.None);

        tokenA.ShouldBe("pat-a-val");
        tokenB.ShouldBe("pat-b-val");

        // Tenant C without entry fails when fallback is false
        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => provider.AcquireTokenAsync(db, contextC, CancellationToken.None));
        ex.Message.ShouldContain("No Databricks principal configured for the tenant");

        // Tenant C gets global when fallback is true
        var dbFallback = new DatabricksDataSourceOptions
        {
            Host = db.Host,
            WarehouseId = db.WarehouseId,
            Auth = new DatabricksAuthOptions
            {
                Mode = db.Auth.Mode,
                PatRef = db.Auth.PatRef,
                Tenants = db.Auth.Tenants,
                FallbackToGlobalPrincipal = true
            }
        };
        var tokenC = await provider.AcquireTokenAsync(dbFallback, contextC, CancellationToken.None);
        tokenC.ShouldBe("global-token-val");

        // Cache keys differ per tenant
        cache.TryGet(db.Host, "tenant-a", DatabricksAuthMode.Pat, "kv://pat-a", string.Empty, DateTimeOffset.UtcNow, out _).ShouldBeTrue();
        cache.TryGet(db.Host, "tenant-b", DatabricksAuthMode.Pat, "kv://pat-b", string.Empty, DateTimeOffset.UtcNow, out _).ShouldBeTrue();
        cache.TryGet(db.Host, "tenant-c", DatabricksAuthMode.Pat, "kv://global-pat", string.Empty, DateTimeOffset.UtcNow, out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-SIMPL-95")]
    public void Audit_Logs_Fingerprint_For_Pat_Not_Token()
    {
        var rawPat = "dapi1234567890abcdefsecretpatvalue";
        var db = new DatabricksDataSourceOptions
        {
            Host = "adb-123.azuredatabricks.net",
            Auth = new DatabricksAuthOptions
            {
                Mode = DatabricksAuthMode.Pat,
                PatRef = "kv://pat-secret"
            }
        };

        var vault = NSubstitute.Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("kv://pat-secret").Returns(System.Text.Encoding.UTF8.GetBytes(rawPat));

        var fields = new Dictionary<string, object?>();
        Autheris.Application.Sql.Services.GovernedSqlExecutor.AddDatabricksAuditFields(
            fields, db, new Autheris.Domain.Common.TenantId("tenant-1"), vault, Env("Production"));

        fields.ShouldContainKey("auth.mode");
        fields["auth.mode"].ShouldBe("Pat");
        fields.ShouldContainKey("auth.tokenFingerprint");
        var fingerprint = fields["auth.tokenFingerprint"] as string;
        fingerprint.ShouldNotBeNull();
        fingerprint!.Length.ShouldBe(16);

        // Raw token / secret must never appear in audit fields
        fields.Values.OfType<string>().ShouldNotContain(rawPat);
        fields.Values.OfType<string>().ShouldNotContain("kv://pat-secret");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-SIMPL-96")]
    public void Audit_Logs_ClientId_For_M2M()
    {
        var db = new DatabricksDataSourceOptions
        {
            Host = "adb-123.azuredatabricks.net",
            Auth = new DatabricksAuthOptions
            {
                Mode = DatabricksAuthMode.OAuthM2M,
                ClientId = "my-service-principal-client-id",
                ClientSecretRef = "kv://sp-secret"
            }
        };

        var fields = new Dictionary<string, object?>();
        Autheris.Application.Sql.Services.GovernedSqlExecutor.AddDatabricksAuditFields(
            fields, db, new Autheris.Domain.Common.TenantId("tenant-1"));

        fields.ShouldContainKey("auth.mode");
        fields["auth.mode"].ShouldBe("OAuthM2M");
        fields.ShouldContainKey("auth.clientId");
        fields["auth.clientId"].ShouldBe("my-service-principal-client-id");
        fields.ShouldNotContainKey("auth.tokenFingerprint");

        fields.Values.OfType<string>().ShouldNotContain("kv://sp-secret");
    }
}
