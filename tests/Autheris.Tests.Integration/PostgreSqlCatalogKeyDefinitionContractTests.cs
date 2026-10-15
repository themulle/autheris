namespace Autheris.Tests.Integration;

using System;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Xunit;

/// <summary>Issue #94: declared key/index contract on PostgreSQL (see <see cref="CatalogKeyDefinitionContractTestsBase"/>).</summary>
public sealed class PostgreSqlCatalogKeyDefinitionContractTests : CatalogKeyDefinitionContractTestsBase, IClassFixture<PostgreSqlCatalogKeyDefinitionContractTests.Fixture>
{
    private readonly Fixture _fixture;

    public PostgreSqlCatalogKeyDefinitionContractTests(Fixture fixture) => _fixture = fixture;

    protected override bool Available => _fixture.Available;
    protected override IGovernanceRepository Repo => _fixture.Repository!;
    protected override DbConnection OpenRaw()
    {
        var conn = new NpgsqlConnection(_fixture.ConnectionString);
        conn.Open();
        return conn;
    }

    protected override void InvalidateCache() => _fixture.Repository!.InvalidateMetadataCacheForTesting();

    public sealed class Fixture : IAsyncLifetime
    {
        private PostgreSqlTestDatabase? _db;
        public bool Available { get; private set; }
        public string ConnectionString => _db!.ConnectionString;
        public PostgreSqlGovernanceRepository? Repository { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                _db = await PostgreSqlTestDatabase.CreateAsync();
                if (!_db.IsAvailable)
                {
                    return;
                }

                var env = Substitute.For<IHostEnvironment>();
                env.EnvironmentName.Returns("Development");
                var options = Options.Create(new GatewayOptions
                {
                    GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
                });
                Repository = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
                Available = true;
            }
            catch (Exception ex)
            {
                Available = false;
                ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
            }
        }

        public async Task DisposeAsync()
        {
            Repository?.Dispose();
            if (_db != null)
            {
                await _db.DisposeAsync();
            }
        }
    }
}
