namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConnectionPolicyTests
{
    private const string SecretCanary = "TlsSecretPassword_492817";

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("PostgreSql", "Host=db.internal;Database=gov;Username=u;Password=" + SecretCanary, "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=gov;Username=u;Password=" + SecretCanary + ";SSL Mode=Disable", "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=gov;Username=u;Password=" + SecretCanary + ";SSL Mode=Prefer", "SSL Mode=VerifyFull")]
    [InlineData("PostgreSql", "Host=db.internal;Database=gov;Username=u;Password=" + SecretCanary + ";SSL Mode=Require", "SSL Mode=VerifyFull")]
    [InlineData("SqlServer", "Server=db.internal;Database=gov;User Id=u;Password=" + SecretCanary + ";Encrypt=False", "Encrypt=Mandatory")]
    [InlineData("SqlServer", "Server=db.internal;Database=gov;User Id=u;Password=" + SecretCanary + ";Encrypt=True;TrustServerCertificate=True", "TrustServerCertificate=true")]
    public void Weak_tls_settings_rejected_outside_development(string provider, string connectionString, string expectedSettingMention)
    {
        var error = ConnectionTlsPolicy.Validate(provider, connectionString);
        error.ShouldNotBeNull();
        error.ShouldContain(expectedSettingMention);
        error.ShouldNotContain(SecretCanary);
        error.ShouldNotContain("db.internal");

        var ex = Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(provider, connectionString, isDevelopment: false));
        ex.Message.ShouldContain(expectedSettingMention);
        ex.Message.ShouldNotContain(SecretCanary);
        ex.Message.ShouldNotContain("db.internal");

        // Development environment allows relaxed local transport when explicitly configured as Development
        Should.NotThrow(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(provider, connectionString, isDevelopment: true));

        // Verified TLS settings succeed outside Development
        Should.NotThrow(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(
                "PostgreSql",
                "Host=db.internal;Database=gov;SSL Mode=VerifyFull",
                isDevelopment: false));

        Should.NotThrow(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(
                "PostgreSql",
                "Host=db.internal;Database=gov;SSL Mode=VerifyCA",
                isDevelopment: false));

        Should.NotThrow(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(
                "SqlServer",
                "Server=db.internal;Database=gov;Encrypt=True;TrustServerCertificate=False",
                isDevelopment: false));
    }
}
