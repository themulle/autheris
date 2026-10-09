namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using Autheris.Api.Middleware;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Gql5AntiEnumerationTests
{
    private static ErrorSanitizingFilter CreateFilter(string envName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
    }

    [Fact]
    public void Production_ForbiddenMessage_DoesNotDiscloseTableNameOrDenialReasons()
    {
        // GQL-5: In production, FORBIDDEN error must not disclose table name or denial reasons
        var filter = CreateFilter("Production");

        var secEx = new GatewayForbiddenException(
            "Access to table 'finance.dbo.salaries' denied: No active consent grants access to table; Casbin ABAC policy denied access; Requires four eyes");

        var rawError = ErrorBuilder.New()
            .SetMessage(secEx.Message)
            .SetCode(secEx.ErrorCode)
            .SetException(secEx)
            .SetExtension("table", "finance.dbo.salaries")
            .SetExtension("deniedReasons", new[] { "Casbin ABAC policy denied access", "Requires four eyes" })
            .Build();

        var sanitized = filter.OnError(rawError);

        sanitized.Code.ShouldBe("FORBIDDEN");
        sanitized.Message.ShouldBe("Access denied.");
        sanitized.Message.ShouldNotContain("salaries");
        sanitized.Message.ShouldNotContain("Casbin");
        sanitized.Message.ShouldNotContain("four eyes");
        if (sanitized.Extensions != null)
        {
            sanitized.Extensions.ShouldNotContain(e => e.Key == "table");
            sanitized.Extensions.ShouldNotContain(e => e.Key == "deniedReasons");
        }
    }

    [Fact]
    public void Production_TableNotFound_MaskedAsGenericAccessDenied()
    {
        // GQL-5: In production, TableNotFoundException must be indistinguishable from a forbidden table
        var filter = CreateFilter("Production");

        var notFoundEx = new TableNotFoundException(new TableIdentifier("secret", "dbo", "classified_data"));

        var rawError = ErrorBuilder.New()
            .SetMessage(notFoundEx.Message)
            .SetCode(notFoundEx.ErrorCode)
            .SetException(notFoundEx)
            .Build();

        var sanitized = filter.OnError(rawError);

        sanitized.Code.ShouldBe("FORBIDDEN");
        sanitized.Message.ShouldBe("Access denied.");
        sanitized.Message.ShouldNotContain("classified_data");
    }

    [Fact]
    public void Development_PreservesDetailedMessageForDebugging()
    {
        // Developers get detailed error messages in Development environment
        var filter = CreateFilter("Development");

        var secEx = new GatewayForbiddenException(
            "Access to table 'finance.dbo.salaries' denied: No active consent grants access to table");

        var rawError = ErrorBuilder.New()
            .SetMessage(secEx.Message)
            .SetCode(secEx.ErrorCode)
            .SetException(secEx)
            .Build();

        var sanitized = filter.OnError(rawError);

        sanitized.Code.ShouldBe("FORBIDDEN");
        sanitized.Message.ShouldContain("salaries");
    }
}
