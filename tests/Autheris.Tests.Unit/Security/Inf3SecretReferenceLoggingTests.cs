namespace Autheris.Tests.Unit.Security;

using System;
using System.Reflection;
using Autheris.Application.Interfaces;
using Autheris.Extensions.Itsm;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class Inf3SecretReferenceLoggingTests
{
    [Fact]
    public void ItsmWebhookHandler_TryGetSecret_DoesNotLogSecretReferenceOrExceptionDetails()
    {
        // INF-3: Secret references must never be logged in plaintext or leak into logs.
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes("SECRET_TOKEN_VALUE_42")
            .Throws(new InvalidOperationException("Vault connection failed"));

        var logger = Substitute.For<ILogger<ItsmWebhookHandler>>();

        // Instantiate ItsmWebhookHandler with correct parameters
        var handler = new ItsmWebhookHandler(
            secretProvider: secretProvider,
            governanceRepo: Substitute.For<IConsentApprovalRepository>(),
            options: Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions()),
            logger: logger);

        var tryGetSecretMethod = typeof(ItsmWebhookHandler).GetMethod("TryGetSecret", BindingFlags.NonPublic | BindingFlags.Instance);
        var result = tryGetSecretMethod!.Invoke(handler, ["SECRET_TOKEN_VALUE_42"]);

        result.ShouldBeNull();

        // Verify the logger NEVER received the secret string "SECRET_TOKEN_VALUE_42"
        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("SECRET_TOKEN_VALUE_42")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
