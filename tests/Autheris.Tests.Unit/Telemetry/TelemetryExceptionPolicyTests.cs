namespace Autheris.Tests.Unit.Telemetry;

using System;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

public sealed class TelemetryExceptionPolicyTests
{
    [Fact]
    public void SafeMessage_AllowsValueFreeExceptions()
    {
        var ex = new TimeoutException("Operation timed out.");
        var safe = TelemetryExceptionPolicy.SafeMessage(ex);
        safe.ShouldBe("Operation timed out.");
    }

    [Fact]
    public void SafeMessage_RedactsUnknownExceptions()
    {
        var ex = new InvalidOperationException("Sensitive internal customer ssn 123-45-6789");
        var safe = TelemetryExceptionPolicy.SafeMessage(ex);
        safe.ShouldBe(TelemetryExceptionPolicy.RedactedPlaceholder);
    }

    [Fact]
    public void ErrorType_ExtractsErrorCode_OrTypeName()
    {
        var tableEx = new GatewayForbiddenException("Access denied.");
        var errType = TelemetryExceptionPolicy.ErrorType(tableEx);
        errType.ShouldBe("FORBIDDEN");

        var timeoutEx = new TimeoutException("Timed out");
        TelemetryExceptionPolicy.ErrorType(timeoutEx).ShouldBe("TimeoutException");
    }

    [Fact]
    public void GetDriverErrorCode_ExtractsNumber_WhenPresent()
    {
        var exWithNumber = new DriverExceptionWithNumber(4060);
        var code = TelemetryExceptionPolicy.GetDriverErrorCode(exWithNumber);
        code.ShouldBe(4060);
    }

    private sealed class DriverExceptionWithNumber(int number) : Exception
    {
        public int Number => number;
    }
}
