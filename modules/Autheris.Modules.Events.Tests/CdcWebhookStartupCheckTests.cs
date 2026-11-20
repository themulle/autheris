namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Application.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CdcWebhookStartupCheckTests
{
    private sealed class Capture : ILogger<CdcWebhookStartupCheck>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static async Task<List<LogLevel>> Run(string env, bool available)
    {
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(available);
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        var log = new Capture();
        await new CdcWebhookStartupCheck(protector, host, log).StartAsync(default);
        return log.Levels;
    }

    [Fact]
    public async Task NonDevelopment_WithoutKek_Warns() => (await Run("Production", false)).ShouldContain(LogLevel.Warning);

    [Fact]
    public async Task NonDevelopment_WithKek_DoesNotWarn() => (await Run("Production", true)).ShouldNotContain(LogLevel.Warning);

    [Fact]
    public async Task Development_WithoutKek_DoesNotWarn() => (await Run("Development", false)).ShouldNotContain(LogLevel.Warning);
}
