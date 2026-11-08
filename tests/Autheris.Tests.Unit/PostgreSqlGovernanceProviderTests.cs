using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class PostgreSqlGovernanceProviderTests
{
    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    [InlineData("PgSql")]
    [InlineData("pgsql")]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("SqlServer")]
    [InlineData("mssql")]
    public void ProviderValidation_AcceptsSupportedProviders(string provider)
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = provider,
                ConnectionString = "Host=localhost;Database=autheris;Username=test;Password=test"
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        Should.NotThrow(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("MySql")]
    [InlineData("MariaDb")]
    [InlineData("Cosmos")]
    [InlineData("InMemory")]
    public void ProviderValidation_RejectsUnsupportedProviders(string provider)
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = provider
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("GovernanceDb Provider");
    }

    [Fact]
    public void MultiNodeClusterMode_RejectsSqlite_WithE2Violation()
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "Sqlite"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                MultiNodeClusterMode = true,
                Replicas = 3
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("E-2");
        ex.Message.ShouldContain("PostgreSql");
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("Postgres")]
    [InlineData("PgSql")]
    public void MultiNodeClusterMode_AllowsPostgreSql(string provider)
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = provider,
                ConnectionString = "Host=localhost;Database=autheris;Username=test;Password=test"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                MultiNodeClusterMode = true,
                Replicas = 3
            },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions
                {
                    Enabled = true,
                    Configuration = "localhost:6379"
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        Should.NotThrow(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("Postgres")]
    [InlineData("PgSql")]
    public void DependencyInjection_RegistersPostgreSqlGovernanceRepository(string provider)
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = provider,
                ConnectionString = "Host=localhost;Database=autheris_test;Username=postgres;Password=secret"
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();

        services.AddGatewayInfrastructure(options);

        // Verify service descriptors without attempting live socket connection to localhost:5432
        services.Any(d => d.ServiceType == typeof(PostgreSqlGovernanceRepository)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IGovernanceRepository)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(ITableMetadataRepository)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IConsentRepository)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IAuditChainExportSource)).ShouldBeTrue();
    }

    [Fact]
    public void DependencyInjection_UnconfiguredProvider_FailsClosed()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "",
                ConnectionString = "Data Source=:memory:"
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();

        // no provider means no governance store and no anchor store: composition or resolution fails closed
        Should.Throw<InvalidOperationException>(() =>
        {
            services.AddGatewayInfrastructure(options);
            using var sp = services.BuildServiceProvider();
            sp.GetService<IGovernanceRepository>();
        });
    }

    [Fact]
    public void PostgreSqlGovernanceRepository_ThrowsOnNullEpochValidationService()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = "Host=localhost;Database=autheris;Username=test;Password=test"
            }
        });

        Should.Throw<ArgumentNullException>(() =>
            new PostgreSqlGovernanceRepository(null!, options, TestEnvironment(), Substitute.For<IKeyVaultSecretProvider>(), Substitute.For<IAuditChainAnchorStore>(), Substitute.For<ICallerContextProvider>()));
    }

    [Fact]
    public void PostgreSqlGovernanceRepository_ThrowsOnNullOptions()
    {
        var epochService = Substitute.For<IEpochValidationService>();

        Should.Throw<ArgumentNullException>(() =>
            new PostgreSqlGovernanceRepository(epochService, null!, TestEnvironment(), Substitute.For<IKeyVaultSecretProvider>(), Substitute.For<IAuditChainAnchorStore>(), Substitute.For<ICallerContextProvider>()));
    }

    private static IHostEnvironment TestEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }
}
