namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// R-DEP-1: the 32-byte minimum for cryptographic keys is enforced where the key is used (independent of how the
/// reference is named), the ForwardAuth secret is checked at startup, and prefix aliases no longer fall back to a
/// global secret.
/// </summary>
public sealed class SecretKeyLengthRDep1Tests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IKeyVaultSecretProvider Provider(string secret)
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes(secret));
        return provider;
    }

    [Fact]
    public void AuditKey_WithNeutralReferenceName_IsLengthChecked()
    {
        // The reference "kv-ref-42" contains neither "hmac" nor "audit"; the provider-side name check would not apply.
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:", AuditHmacKeyVaultRef = "kv-ref-42" }
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            new SqliteGovernanceRepository(Substitute.For<IEpochValidationService>(), options, Env(Environments.Production), Provider("short-key")));

        ex.Message.ShouldContain("32");
    }

    [Fact]
    public async Task ForwardAuthSecret_FromKeyVaultRef_IsCheckedAtStartup()
    {
        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions { Enabled = true, SharedSecretKeyVaultRef = "kv-ref-7" }
            }
        });

        var shortValidator = new ForwardAuthSecretStartupValidator(options, Env(Environments.Production), Provider("too-short"));
        var longValidator = new ForwardAuthSecretStartupValidator(options, Env(Environments.Production), Provider(new string('k', 32)));
        var devValidator = new ForwardAuthSecretStartupValidator(options, Env(Environments.Development), Provider("too-short"));

        await Should.ThrowAsync<InvalidOperationException>(() => shortValidator.StartAsync(CancellationToken.None));
        await Should.NotThrowAsync(() => longValidator.StartAsync(CancellationToken.None));
        await Should.NotThrowAsync(() => devValidator.StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("audit:tenant-x", "AUDIT_HMAC_KEY")]
    [InlineData("forwardauth:edge-2", "FORWARDAUTH_SHARED_SECRET")]
    [InlineData("hmac:tenant-x", "HMAC_SECRET")]
    public void PrefixedReference_DoesNotResolveToGlobalSecret(string secretRef, string globalKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [globalKey] = new string('g', 40) })
            .Build();
        var provider = new DefaultEnvironmentSecretProvider(configuration, Env(Environments.Production));

        Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes(secretRef));
    }

    [Theory]
    [InlineData("audit-hmac-key", "AUDIT_HMAC_KEY")]
    [InlineData("forwardauth-secret", "FORWARDAUTH_SHARED_SECRET")]
    [InlineData("hmac-masking-secret", "HMAC_SECRET")]
    public void ExactWellKnownReference_StillResolves(string secretRef, string globalKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [globalKey] = new string('g', 40) })
            .Build();
        var provider = new DefaultEnvironmentSecretProvider(configuration, Env(Environments.Production));

        provider.GetSecretBytes(secretRef).Length.ShouldBe(40);
    }
}
