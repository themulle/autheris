namespace Autheris.Tests.Unit.Hosting;

using System;
using Autheris.Api.Configuration.Validation;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.State;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DistributedStateTests
{
    private static IHostEnvironment Env(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }

    [Fact]
    public void Missing_shared_state_fails_closed_in_production_for_revocation_replay_ratelimit()
    {
        var env = Env(Environments.Production);
        var validator = new GatewayStorageOptionsValidator(env);
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=localhost;Database=autheris;Username=postgres;Password=postgres"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                Replicas = 2,
                MultiNodeClusterMode = false
            },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = false }
            }
        };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue("Multi-node without Redis must fail validation in production.");
        result.FailureMessage.ShouldContain("token revocation");
        result.FailureMessage.ShouldContain("replay cache");
        result.FailureMessage.ShouldContain("rate limit");
    }

    [Fact]
    public void MultiNode_without_shared_state_fails_for_lockout_and_deprovisioning()
    {
        var env = Env(Environments.Production);
        var validator = new GatewayStorageOptionsValidator(env);
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=localhost;Database=autheris;Username=postgres;Password=postgres"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                MultiNodeClusterMode = true,
                Replicas = 1
            },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = false }
            }
        };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue("Multi-node cluster mode without Redis must fail validation.");
        result.FailureMessage.ShouldContain("subject lockout cache");
        result.FailureMessage.ShouldContain("deprovisioning");

        // When Redis is enabled for multi-node, external shared state is wired; in-memory fallback types are not registered
        var services = new ServiceCollection();
        var validMultiNodeOptions = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=localhost;Database=autheris;Username=postgres;Password=postgres"
            },
            HighAvailability = new HighAvailabilityOptions { MultiNodeClusterMode = true },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions
                {
                    Enabled = true,
                    Configuration = "localhost:6379,abortConnect=false"
                }
            }
        };

        services.AddAutherisStorage(validMultiNodeOptions, env);
        var descriptorTypes = services.Select(d => d.ImplementationType ?? d.ServiceType).ToList();
        descriptorTypes.ShouldNotContain(typeof(InMemoryTokenRevocationService));
        descriptorTypes.ShouldNotContain(typeof(InMemoryRateLimiterService));
        descriptorTypes.ShouldNotContain(typeof(InProcessChannelEventBus));
    }

    [Fact]
    public void SingleNode_starts_with_in_memory_state()
    {
        var env = Env(Environments.Production);
        var validator = new GatewayStorageOptionsValidator(env);
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=localhost;Database=autheris;Username=postgres;Password=postgres"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                Replicas = 1,
                MultiNodeClusterMode = false
            },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = false }
            }
        };

        var result = validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue("Single-node is allowed without external Redis in production.");

        var services = new ServiceCollection();
        services.AddAutherisStorage(options, env);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ITokenRevocationService>().ShouldBeOfType<InMemoryTokenRevocationService>();
        sp.GetRequiredService<IRateLimiterService>().ShouldBeOfType<InMemoryRateLimiterService>();
        sp.GetRequiredService<IEventBus>().ShouldBeOfType<InProcessChannelEventBus>();
        sp.GetRequiredService<IClusterStateTopology>().Mode.ShouldBe(ClusterStateMode.InMemory);
        sp.GetRequiredService<IClusterStateTopology>().IsClusterWide.ShouldBeFalse();
    }

    [Fact]
    public void Development_starts_with_warning()
    {
        var env = Env(Environments.Development);
        var validator = new GatewayStorageOptionsValidator(env);
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = "Host=localhost;Database=autheris;Username=postgres;Password=postgres"
            },
            HighAvailability = new HighAvailabilityOptions
            {
                Replicas = 2,
                MultiNodeClusterMode = true
            },
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = false }
            }
        };

        var result = validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue("In development, multi-node without Redis does not fail startup validation.");
    }
}
