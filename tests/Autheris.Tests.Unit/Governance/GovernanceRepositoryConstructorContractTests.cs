using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>ARC-I-13: every security-relevant repository dependency is required; AU-01/AU-03 are enforced by the anchor-store factory.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceRepositoryConstructorContractTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions Gateway(
        string provider,
        string? connectionString = null,
        string? anchorPath = null,
        string? auditKeyRef = null,
        bool secureTransport = false) => new()
    {
        GovernanceDb = new GovernanceDbOptions
        {
            Provider = provider,
            AuditHmacKeyVaultRef = auditKeyRef,
            ConnectionString = connectionString ?? provider switch
            {
                "PostgreSql" => secureTransport
                    ? "Host=db.example;Database=x;Username=u;Password=p;SSL Mode=VerifyFull"
                    : "Host=localhost;Database=x;Username=u;Password=p",
                "SqlServer" => secureTransport
                    ? "Server=db.example;Database=x;User Id=u;Password=p;Encrypt=Strict;TrustServerCertificate=false"
                    : "Server=localhost;Database=x;User Id=u;Password=p;TrustServerCertificate=true",
                _ => $"Data Source=ctor_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
            },
        },
        Audit = new AuditOptions { ChainAnchorPath = anchorPath ?? string.Empty },
    };

    private static object Construct(string backend, string? nullParameter, string environmentName = "Test", IAuditChainAnchorStore? anchor = null)
    {
        var epoch = nullParameter == "epoch" ? null! : Substitute.For<IEpochValidationService>();
        var options = nullParameter == "options" ? null! : Options.Create(Gateway(backend));
        var env = nullParameter == "environment" ? null! : Env(environmentName);
        var secrets = nullParameter == "secrets" ? null! : Substitute.For<IKeyVaultSecretProvider>();
        var store = nullParameter == "anchor" ? null! : anchor ?? new InMemoryAuditChainAnchorStore();
        var caller = nullParameter == "caller" ? null! : Substitute.For<ICallerContextProvider>();
        return backend switch
        {
            "PostgreSql" => new PostgreSqlGovernanceRepository(epoch, options, env, secrets, store, caller),
            "SqlServer" => new SqlServerGovernanceRepository(epoch, options, env, secrets, store, caller),
            _ => new SqliteGovernanceRepository(epoch, options, env, secrets, store, caller),
        };
    }

    public static TheoryData<string, string> NullCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var backend in new[] { "Sqlite", "PostgreSql", "SqlServer" })
        {
            foreach (var parameter in new[] { "epoch", "options", "environment", "secrets", "anchor", "caller" })
            {
                data.Add(backend, parameter);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NullCases))]
    public void Constructor_NullRequiredDependency_Throws(string backend, string parameter)
    {
        Should.Throw<ArgumentNullException>(() => Construct(backend, parameter));
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Constructor_WithAllDependencies_Succeeds(string backend)
    {
        var repository = Construct(backend, null);

        (repository as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void InMemoryAnchorStore_OutsideDevOrTest_Throws(string backend)
    {
        // The audit HMAC key is resolved first, so a key reference is configured to reach the anchor check.
        var options = Gateway(backend, auditKeyRef: "vault://audit", secureTransport: true);
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes("vault://audit").Returns(new byte[32]);
        var epoch = Substitute.For<IEpochValidationService>();
        var caller = Substitute.For<ICallerContextProvider>();

        var ex = Should.Throw<InvalidOperationException>(() => backend == "PostgreSql"
            ? new PostgreSqlGovernanceRepository(epoch, Options.Create(options), Env("Production"), secrets, new InMemoryAuditChainAnchorStore(), caller)
            : (object)new SqlServerGovernanceRepository(epoch, Options.Create(options), Env("Production"), secrets, new InMemoryAuditChainAnchorStore(), caller));

        ex.Message.ShouldContain("AU-01/AU-03");
    }

    [Fact]
    public void AnchorFactory_ProductionWithoutPersistentAnchor_ThrowsAu01()
    {
        var options = Gateway("PostgreSql");

        var ex = Should.Throw<InvalidOperationException>(() =>
            AuditChainAnchorStoreFactory.CreateForGovernance(options, Env("Production"), Substitute.For<IKeyVaultSecretProvider>(), null, null));

        ex.Message.ShouldContain("AU-01/AU-03");
        ex.Message.ShouldContain("persistent audit anchor store");
    }

    [Fact]
    public void AnchorFactory_ProductionWithAnchorPath_ReturnsFileStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}.json");
        var options = Gateway("PostgreSql", anchorPath: path);

        var store = AuditChainAnchorStoreFactory.CreateForGovernance(options, Env("Production"), Substitute.For<IKeyVaultSecretProvider>(), null, null);

        store.ShouldBeOfType<FileAuditChainAnchorStore>().FilePath.ShouldBe(Path.GetFullPath(path));
    }

    [Fact]
    public void AnchorFactory_DevelopmentWithoutAnchor_UsesInMemoryStore()
    {
        var store = AuditChainAnchorStoreFactory.CreateForGovernance(Gateway("PostgreSql"), Env("Development"), Substitute.For<IKeyVaultSecretProvider>(), null, null);

        store.ShouldBeOfType<InMemoryAuditChainAnchorStore>();
    }

    [Fact]
    public void AnchorFactory_SqliteFileDatabase_DefaultsNextToTheDatabase()
    {
        var options = Gateway("Sqlite", "Data Source=/tmp/autheris-anchor-test/gov.db");

        var store = AuditChainAnchorStoreFactory.CreateForGovernance(options, Env("Test"), Substitute.For<IKeyVaultSecretProvider>(), null, null);

        store.ShouldBeOfType<FileAuditChainAnchorStore>().FilePath.ShouldBe("/tmp/autheris-anchor-test/gov.db.audit-anchor.json");
    }

    [Theory]
    [InlineData("PostgreSql", typeof(InMemoryAuditChainAnchorStore))]
    [InlineData("SqlServer", typeof(InMemoryAuditChainAnchorStore))]
    [InlineData("Sqlite", typeof(InMemoryAuditChainAnchorStore))]
    public void AddAutherisGovernance_RegistersAnchorStore_PerProvider(string provider, Type expected)
    {
        var options = Gateway(provider);
        var services = new ServiceCollection();
        services.AddSingleton(Env("Development"));
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.AddAutherisGovernance(options);

        services.Any(d => d.ServiceType == typeof(IAuditChainAnchorStore)).ShouldBeTrue();
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAuditChainAnchorStore>().ShouldBeOfType(expected);
    }

    [Fact]
    public void AddAutherisGovernance_SqliteFileDatabase_RegistersFileAnchorNextToDatabase()
    {
        var options = Gateway("Sqlite", "Data Source=/tmp/autheris-anchor-test/reg.db");
        var services = new ServiceCollection();
        services.AddSingleton(Env("Test"));
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.AddAutherisGovernance(options);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAuditChainAnchorStore>().ShouldBeOfType<FileAuditChainAnchorStore>()
            .FilePath.ShouldBe("/tmp/autheris-anchor-test/reg.db.audit-anchor.json");
    }
}
