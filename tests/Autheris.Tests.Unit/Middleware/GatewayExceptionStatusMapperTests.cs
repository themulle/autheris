namespace Autheris.Tests.Unit.Middleware;

using System;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed class GatewayExceptionStatusMapperTests
{
    [Fact]
    public void Resolve_Null_Returns500()
    {
        GatewayExceptionStatusMapper.Resolve(null!).ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public void Resolve_GenericException_Returns500()
    {
        GatewayExceptionStatusMapper.Resolve(new InvalidOperationException("boom")).ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public void Resolve_TimeoutException_Returns504()
    {
        GatewayExceptionStatusMapper.Resolve(new TimeoutException()).ShouldBe(StatusCodes.Status504GatewayTimeout);
    }

    [Fact]
    public void Resolve_GatewayInvalidQueryException_Returns400()
    {
        GatewayExceptionStatusMapper.Resolve(new GatewayInvalidQueryException("invalid")).ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void Resolve_GatewayThrottledException_Returns429()
    {
        GatewayExceptionStatusMapper.Resolve(new GatewayThrottledException(5)).ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public void Resolve_AuditUnavailableException_Returns503()
    {
        GatewayExceptionStatusMapper.Resolve(new Autheris.Application.Audit.AuditUnavailableException("unavailable")).ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public void Resolve_GatewayForbiddenException_Returns403()
    {
        GatewayExceptionStatusMapper.Resolve(new GatewayForbiddenException("Access denied", "DENIED")).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Resolve_GatewayUnauthorizedException_Returns401()
    {
        GatewayExceptionStatusMapper.Resolve(new GatewayUnauthorizedException("Unauthorized", "UNAUTHORIZED")).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void Resolve_AntlrParseCanceledException_Returns400()
    {
        GatewayExceptionStatusMapper.Resolve(new Antlr4.Runtime.Misc.ParseCanceledException()).ShouldBe(StatusCodes.Status400BadRequest);
    }
}
