using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class EnvironmentSecretProviderTests
{
    [Fact]
    public void DefaultEnvironmentSecretProvider_ResolvesExactCandidatesWithoutCollisions()
    {
        // Arrange: Config has both a generic secret and the dedicated hmac masking secret
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string?>("HMAC_SECRET", "hmac-secret-12345678901234567890123456789012"),
            new System.Collections.Generic.KeyValuePair<string, string?>("SOME_OTHER_SECRET", "other-999")
        });
        var configuration = configBuilder.Build();

        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var provider = new DefaultEnvironmentSecretProvider(configuration, mockEnv);

        // Act
        var secretBytes = provider.GetSecretBytes("hmac-masking-secret");

        // Assert
        Encoding.UTF8.GetString(secretBytes).ShouldBe("hmac-secret-12345678901234567890123456789012");
    }

    [Fact]
    public void L01_DefaultEnvironmentSecretProvider_ResolvingFromConfiguration_LogsWarning()
    {
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["test_secret"] = "SuperSecretValue123"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>>();

        var provider = new DefaultEnvironmentSecretProvider(config, env, logger);
        var bytes = provider.GetSecretBytes("test_secret");

        Encoding.UTF8.GetString(bytes).ShouldBe("SuperSecretValue123");
        logger.ReceivedWithAnyArgs().Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            default,
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void INF_3_DefaultEnvironmentSecretProvider_RedactsSecretReferenceInLogStatements()
    {
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["AUDIT_HMAC_KEY"] = "SuperSecretValue12345678901234567890"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var logger = new TestSecretProviderLogger();

        var provider = new DefaultEnvironmentSecretProvider(config, env, logger);
        provider.GetSecretBytes("audit-hmac-key");

        logger.Messages.Count.ShouldBeGreaterThan(0);
        foreach (var msg in logger.Messages)
        {
            msg.ShouldNotContain("audit-hmac-key");
        }
        logger.Messages.ShouldContain(m => m.Contains("Reference with length 14"));
    }

    private sealed class TestSecretProviderLogger : Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>
    {
        public readonly System.Collections.Generic.List<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
