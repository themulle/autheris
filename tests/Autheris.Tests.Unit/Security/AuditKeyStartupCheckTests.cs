namespace Autheris.Tests.Unit.Security;

using System.Text;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>L-2: the audit digest key is validated at startup outside Development and Test.</summary>
public sealed class AuditKeyStartupCheckTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static AuditKeyStartupCheck Check(string environment, IKeyVaultSecretProvider? secrets, string? masterRef = "GQL-HMAC")
    {
        var options = Options.Create(new GatewayOptions { DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = masterRef ?? string.Empty } });
        return new AuditKeyStartupCheck(new CompiledSqlAuditDigester(options, secrets, Env(environment)));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NoKeyConfigured_OutsideDevelopmentAndTest_FailsTheStart(string environment)
    {
        var check = Check(environment, secrets: null, masterRef: null);

        await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VaultFailure_InProduction_FailsTheStart()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("vault down"));

        await Should.ThrowAsync<InvalidOperationException>(() => Check("Production", secrets).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TooShortKey_InProduction_FailsTheStart()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes("GQL-HMAC").Returns(Encoding.UTF8.GetBytes("short"));

        await Should.ThrowAsync<Exception>(() => Check("Production", secrets).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UsableKey_InProduction_Starts()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes("GQL-HMAC").Returns(Encoding.UTF8.GetBytes("master-hmac-secret-0123456789abcdef"));

        await Should.NotThrowAsync(() => Check("Production", secrets).StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task NoKeyConfigured_InDevelopmentAndTest_Starts(string environment)
    {
        await Should.NotThrowAsync(() => Check(environment, secrets: null, masterRef: null).StartAsync(CancellationToken.None));
    }
}
