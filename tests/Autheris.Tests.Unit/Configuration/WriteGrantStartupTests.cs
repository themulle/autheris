using System;
using System.Collections.Generic;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Configuration;

public sealed class WriteGrantStartupTests
{
    private static IWebHostEnvironment Env(string name)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("Gateway:WebSql:DmlWriterRoles:0", "Writer")]
    [InlineData("Gateway:WebSql:DmlWriterRoles", "")]
    [InlineData("Gateway:WebSql:DmlWriterRoles", "[]")]
    public void DmlWriterRolesKey_FailsStartup(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var violations = LegacySwitchGuard.FindViolations(config);
        violations.ShouldContain(v => v.Contains("Gateway:WebSql:DmlWriterRoles", StringComparison.Ordinal));

        var ex = Should.Throw<InvalidOperationException>(() => LegacySwitchGuard.ThrowIfLegacyKeysSet(config));
        ex.Message.ShouldContain("Gateway:WebSql:DmlWriterRoles");
        ex.Message.ShouldContain("WriteRules");
    }

    [Fact]
    public void RemovedMap_IncludesDmlWriterRoles_AndAllowsDmlWithoutRoles()
    {
        LegacySwitchGuard.Removed.ContainsKey("WebSql:DmlWriterRoles").ShouldBeTrue();

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = true },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" },
            Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), _ => null));
    }
}
