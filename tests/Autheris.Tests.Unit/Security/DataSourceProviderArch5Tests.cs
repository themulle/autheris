namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// Architecture 5 / SQL2-22: one mapping from a data source provider name to a dialect. Before, the connection
/// factory, the TLS policy, WebSQL and the procedure path each had their own alias list ("pgsql" was PostgreSQL for
/// procedures, unsupported for the factory and skipped by the TLS policy).
/// </summary>
public sealed class DataSourceProviderArch5Tests
{
    [Theory]
    [InlineData(null, DatabaseDialect.Sqlite)]
    [InlineData("", DatabaseDialect.Sqlite)]
    [InlineData("  ", DatabaseDialect.Sqlite)]
    [InlineData("Sqlite", DatabaseDialect.Sqlite)]
    [InlineData("sqlite3", DatabaseDialect.Sqlite)]
    [InlineData("SqlServer", DatabaseDialect.SqlServer)]
    [InlineData("mssql", DatabaseDialect.SqlServer)]
    [InlineData("Microsoft SQL Server", DatabaseDialect.SqlServer)]
    [InlineData("PostgreSql", DatabaseDialect.PostgreSql)]
    [InlineData("Postgres", DatabaseDialect.PostgreSql)]
    [InlineData("npgsql", DatabaseDialect.PostgreSql)]
    [InlineData("pgsql", DatabaseDialect.PostgreSql)]
    public void TryResolveDialect_KnownProviders(string? provider, DatabaseDialect expected)
    {
        DataSourceProvider.TryResolveDialect(provider, out var dialect).ShouldBeTrue();
        dialect.ShouldBe(expected);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("SqlServer, Sqlite")]
    [InlineData("99")]
    public void TryResolveDialect_UnknownProviders_AreRejected(string provider)
    {
        DataSourceProvider.TryResolveDialect(provider, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("pgsql")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [InlineData("sqlite3")]
    public void AllPaths_AgreeOnTheDialect(string provider)
    {
        DataSourceProvider.TryResolveDialect(provider, out var expected).ShouldBeTrue();

        ProcedureConnectionProvider.TryResolveDialect(provider, out var procedureDialect).ShouldBeTrue();
        procedureDialect.ShouldBe(expected);
    }

    [Fact]
    public void TlsPolicy_AppliesToEveryPostgreSqlAlias()
    {
        ConnectionTlsPolicy.Validate("pgsql", "Host=db;Database=x;SSL Mode=Disable").ShouldNotBeNull();
        ConnectionTlsPolicy.Validate("pgsql", "Host=db;Database=x;SSL Mode=VerifyFull").ShouldBeNull();
    }

    [Fact]
    public void TlsPolicy_UnknownProvider_IsRejected()
    {
        // An unknown provider cannot be opened anyway; the policy must not silently pass it.
        ConnectionTlsPolicy.Validate("mysql", "Server=db").ShouldNotBeNull();
    }

    [Fact]
    public async Task ConnectionFactory_OpensEveryPostgreSqlAlias()
    {
        var factory = new SqlConnectionFactory();
        var options = new DataSourceConnectionOptions
        {
            Provider = "pgsql",
            ConnectionString = "Host=127.0.0.1;Port=1;Database=x;Timeout=1"
        };

        // The provider is accepted; the open fails only because nothing listens on port 1.
        var ex = await Should.ThrowAsync<Exception>(() => factory.CreateOpenConnectionAsync(options));
        ex.ShouldNotBeOfType<NotSupportedException>();
    }

    [Theory]
    [InlineData("Databricks")]
    public async Task ConnectionFactory_DialectsWithoutDriver_AreNotSupported(string provider)
    {
        var factory = new SqlConnectionFactory();
        var options = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" };

        await Should.ThrowAsync<NotSupportedException>(() => factory.CreateOpenConnectionAsync(options));
    }
}
