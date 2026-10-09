namespace Autheris.Tests.Unit.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// POL-9 follow-up: EpochValidationService needs the table metadata in degraded mode, and the governance repository
/// needs the epoch service. Taking the repository in the constructor made the singletons depend on each other and the
/// application never finished starting. The service resolves the repository lazily instead.
/// </summary>
public sealed class EpochValidationDependencyCycleTests
{
    [Fact]
    public async Task SqliteProvider_ResolvesEpochServiceAndRepository_WithoutDeadlock()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source=epoch_cycle_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddGatewayInfrastructure(options);

        var resolve = Task.Run(async () =>
        {
            await using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IEpochValidationService>().ShouldNotBeNull();
            provider.GetRequiredService<IGovernanceRepository>().ShouldNotBeNull();
        });

        await resolve.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
