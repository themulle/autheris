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

        // Q-SECID-2: VerifyCA is rejected outside Development (VerifyFull only)
        Should.Throw<InvalidOperationException>(() =>
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

    [Fact]
    [Trait("Category", "Security")]
    public void PostgreSql_VerifyCA_rejected_outside_development()
    {
        const string conn = "Host=db.internal;Database=gov;Username=u;Password=" + SecretCanary + ";SSL Mode=VerifyCA";

        var error = ConnectionTlsPolicy.Validate("PostgreSql", conn);
        error.ShouldNotBeNull();
        error.ShouldContain("SSL Mode=VerifyFull");
        error.ShouldNotContain("VerifyCA");
        error.ShouldNotContain(SecretCanary);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("PostgreSql", conn, isDevelopment: false));
        ex.Message.ShouldContain("SSL Mode=VerifyFull");
        ex.Message.ShouldNotContain("VerifyCA");
        ex.Message.ShouldNotContain(SecretCanary);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PostgreSql_VerifyFull_accepted()
    {
        const string conn = "Host=db.internal;Database=gov;SSL Mode=VerifyFull";

        ConnectionTlsPolicy.Validate("PostgreSql", conn).ShouldBeNull();
        Should.NotThrow(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("PostgreSql", conn, isDevelopment: false));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Host=db.internal;Database=gov;Password=" + SecretCanary + ";SSL Mode=VerifyFull;Trust Server Certificate=true")]
    [InlineData("Host=db.internal;Database=gov;Password=" + SecretCanary + ";SSL Mode=VerifyFull;TrustServerCertificate=true")]
    public void Postgres_trust_server_certificate_rejected(string connectionString)
    {
        var error = ConnectionTlsPolicy.Validate("PostgreSql", connectionString);
        error.ShouldNotBeNull();
        error.ShouldNotContain(SecretCanary);
        error.ShouldNotContain("db.internal");

        var ex = Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("PostgreSql", connectionString, isDevelopment: false));
        ex.Message.ShouldNotContain(SecretCanary);
        ex.Message.ShouldNotContain("db.internal");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SqlServer_unparseable_connection_string_rejected()
    {
        var malformed = "not a valid connection string = ;;; " + SecretCanary;

        var error = ConnectionTlsPolicy.Validate("SqlServer", malformed);
        error.ShouldNotBeNull();
        error.ShouldContain("Encrypt=Mandatory");
        error.ShouldNotContain(SecretCanary);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("SqlServer", malformed, isDevelopment: false));
        ex.Message.ShouldNotContain(SecretCanary);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Server=db.internal;Database=gov;Password=" + SecretCanary + ";Encrypt=Optional")]
    [InlineData("Server=db.internal;Database=gov;Password=" + SecretCanary + ";Encrypt=Mandatory;TrustServerCertificate=true")]
    [InlineData("Server=db.internal;Database=gov;Password=" + SecretCanary + ";Encrypt=Strict;TrustServerCertificate=true")]
    public void SqlServer_weak_tls_settings_rejected(string connectionString)
    {
        var error = ConnectionTlsPolicy.Validate("SqlServer", connectionString);
        error.ShouldNotBeNull();
        error.ShouldNotContain(SecretCanary);
        error.ShouldNotContain("db.internal");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("User Id=app;Data Source=db.internal:1521/APP", false)]
    [InlineData("User Id=app;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db.internal)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=APP)))", false)]
    [InlineData("User Id=app;Data Source=tcps://db.internal:2484/APP", true)]
    [InlineData("User Id=app;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.internal)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=APP)))", true)]
    public void Oracle_without_tcps_rejected_by_tls_policy(string connectionString, bool expectedValid)
    {
        var error = ConnectionTlsPolicy.Validate("Oracle", connectionString);
        if (expectedValid)
        {
            error.ShouldBeNull();
        }
        else
        {
            error.ShouldNotBeNull();
            error.ShouldContain("TCPS");
            error.ShouldNotContain("db.internal");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Postgres_unparseable_connection_string_rejected()
    {
        var malformed = "not a valid connection string = ;;; " + SecretCanary;

        var error = ConnectionTlsPolicy.Validate("PostgreSql", malformed);
        error.ShouldNotBeNull();
        error.ShouldContain("SSL Mode=VerifyFull");
        error.ShouldNotContain(SecretCanary);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("PostgreSql", malformed, isDevelopment: false));
        ex.Message.ShouldNotContain(SecretCanary);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Every_dialect_has_explicit_tls_rule()
    {
        foreach (var dialect in Enum.GetValues<Autheris.Domain.Common.DatabaseDialect>())
        {
            var probe = dialect switch
            {
                Autheris.Domain.Common.DatabaseDialect.PostgreSql => "Host=db.internal;Database=gov;SSL Mode=Require",
                Autheris.Domain.Common.DatabaseDialect.SqlServer => "Server=db.internal;Database=gov;Encrypt=Optional",
                Autheris.Domain.Common.DatabaseDialect.Oracle => "User Id=app;Data Source=db.internal:1521/APP",
                _ => "Data Source=local.db"
            };

            var result = ConnectionTlsPolicy.ValidateDialect(dialect, probe);
            if (dialect is Autheris.Domain.Common.DatabaseDialect.Sqlite or Autheris.Domain.Common.DatabaseDialect.Databricks)
            {
                result.ShouldBeNull();
            }
            else
            {
                result.ShouldNotBeNull();
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async System.Threading.Tasks.Task ConnectionFactory_rejects_weak_tls_outside_development()
    {
        var factory = new SqlConnectionFactory();

        await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateOpenConnectionAsync(
            new Autheris.Domain.Options.DataSourceConnectionOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=db.internal;Database=gov;SSL Mode=VerifyCA"
            }));

        await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateOpenConnectionAsync(
            new Autheris.Domain.Options.DataSourceConnectionOptions
            {
                Provider = "SqlServer",
                ConnectionString = "Server=db.internal;Database=gov;Encrypt=Mandatory;TrustServerCertificate=true"
            }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unknown_dialect_value_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ConnectionTlsPolicy.ValidateDialect((Autheris.Domain.Common.DatabaseDialect)999, "Host=db.internal;Database=gov"));

        ConnectionTlsPolicy.Validate("unknown-engine", "Host=db.internal;Database=gov").ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_sqlite_and_null_provider_have_no_sqlite_exemption()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls("Sqlite", "Data Source=/var/lib/gov.db", isDevelopment: false));

        Should.Throw<InvalidOperationException>(() =>
            ConnectionTlsPolicy.ValidateGovernanceConnectionTls(null, "Host=db.internal;Database=gov", isDevelopment: false));
    }
}
