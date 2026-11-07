namespace Autheris.Tests.Unit.State;

using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class ClusterStateTopologyTests
{
    [Theory]
    [InlineData(ClusterStateMode.InMemory, false)]
    [InlineData(ClusterStateMode.External, true)]
    public void IsClusterWide_OnlyForExternalState(ClusterStateMode mode, bool expected)
    {
        new ClusterStateTopology(mode).IsClusterWide.ShouldBe(expected);
    }

    private static GatewayOptions Options(bool redis, int replicas, bool multiNode = false) => new()
    {
        GovernanceDb = new GovernanceDbOptions
        {
            Provider = "PostgreSql",
            ConnectionString = "Host=localhost;Database=autheris;Username=test;Password=test"
        },
        HighAvailability = new HighAvailabilityOptions { Replicas = replicas, MultiNodeClusterMode = multiNode },
        Caching = new CachingOptions
        {
            Redis = new RedisOptions { Enabled = redis, Configuration = "localhost:6379" }
        }
    };

    private static IHostEnvironment Dev()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }

    [Fact]
    public void Validator_Allows_Replicas2_WithExternalRedis()
    {
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(Options(redis: true, replicas: 2), Dev()));
    }

    [Fact]
    public void Validator_Allows_SingleReplica_WithoutRedis()
    {
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(Options(redis: false, replicas: 1), Dev()));
    }
}
