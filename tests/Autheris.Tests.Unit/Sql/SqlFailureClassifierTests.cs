namespace Autheris.Tests.Unit.Sql;

using System;
using System.Data.Common;
using System.Security;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.DataSources;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class SqlFailureClassifierTests
{
    private sealed class FakeDbTimeoutException : DbException
    {
        public FakeDbTimeoutException(string message) : base(message) { }
    }

    private sealed class FakeDbGenericException : DbException
    {
        public FakeDbGenericException(string message) : base(message) { }
    }

    public static TheoryData<Exception, SqlFailureKind, string> ExceptionFamilyData => new()
    {
        // Compiler family
        {
            new SqlSyntaxErrorException(1, 1, "line 1: mismatched token"),
            SqlFailureKind.InvalidQuery,
            GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.SyntaxError)
        },
        // Security family
        {
            new SecurityException("Tenant access forbidden"),
            SqlFailureKind.PolicyDenied,
            GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.PolicyViolation)
        },
        // Transport family
        {
            new TransportNotAllowedException("restricted_source", DataSourceTransportFlags.Rest),
            SqlFailureKind.TransportDenied,
            "Access to the data source is not allowed over this transport."
        },
        // Timeout family (TimeoutException)
        {
            new TimeoutException("Statement timed out after 30 seconds"),
            SqlFailureKind.Timeout,
            SqlFailureClassifier.GenericTimeoutMessage
        },
        // Timeout family (DbException with "Timeout")
        {
            new FakeDbTimeoutException("Execution Timeout Expired. The timeout period elapsed."),
            SqlFailureKind.Timeout,
            SqlFailureClassifier.GenericTimeoutMessage
        },
        // Throttled family
        {
            new GatewayThrottledException("Too many concurrent requests", 5),
            SqlFailureKind.Throttled,
            SqlFailureClassifier.GenericThrottledMessage
        },
        // NotSupported family
        {
            new NotSupportedException("Operation is not supported"),
            SqlFailureKind.NotSupported,
            SqlFailureClassifier.GenericNotSupportedMessage
        },
        // Generic / Internal family
        {
            new InvalidOperationException("Fatal database driver crash"),
            SqlFailureKind.Internal,
            SqlFailureClassifier.GenericServerErrorMessage
        }
    };

    [Theory]
    [MemberData(nameof(ExceptionFamilyData))]
    public void Classify_MapsEachExceptionFamily_ToKindAndFixedMessage(Exception ex, SqlFailureKind expectedKind, string expectedMessage)
    {
        var failure = SqlFailureClassifier.Classify(ex);

        failure.Kind.ShouldBe(expectedKind);
        failure.FixedMessage.ShouldBe(expectedMessage);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("SUPER_SECRET_TOKEN_12345")]
    [InlineData("DATABASE_PASSWORD_XYZ")]
    [InlineData("SECRET_CREDIT_CARD_4111")]
    public void Classify_NeverReturnsExceptionMessage(string secret)
    {
        Exception[] sensitiveExceptions =
        [
            new SqlSyntaxErrorException(1, 1, $"Syntax error near '{secret}'"),
            new SecurityException($"Access to {secret} denied"),
            new TransportNotAllowedException(secret, DataSourceTransportFlags.Rest),
            new TimeoutException($"Query {secret} timed out"),
            new FakeDbTimeoutException($"Timeout connecting to {secret}"),
            new GatewayThrottledException($"Throttled for {secret}", 5),
            new NotSupportedException($"Feature {secret} not supported"),
            new GatewayNotImplementedException($"Feature {secret} not implemented"),
            new ArgumentException($"Bad arg {secret}"),
            new FakeDbGenericException($"Fatal error in {secret}"),
            new InvalidOperationException($"Crash in {secret}"),
            new OperationCanceledException($"Canceled query {secret}")
        ];

        foreach (var ex in sensitiveExceptions)
        {
            var failure = SqlFailureClassifier.Classify(ex);

            failure.FixedMessage.ShouldNotBeNullOrWhiteSpace();
            failure.FixedMessage.ShouldNotContain(secret);
        }
    }
}
