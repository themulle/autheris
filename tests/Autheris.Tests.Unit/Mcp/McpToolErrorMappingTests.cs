namespace Autheris.Tests.Unit.Mcp;

using System.Security;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>query_dataset failures answer with the code the HTTP dataset endpoint returns for the same input, and a denied dataset is one answer.</summary>
public sealed class McpToolErrorMappingTests
{
    private static string Mapped(Exception ex)
    {
        McpToolError.TryMapQueryDatasetFailure(ex, out var envelope).ShouldBeTrue(ex.GetType().Name);
        return envelope;
    }

    [Fact]
    public async Task QueryDataset_FilterSyntaxError_InvalidQuery()
    {
        Exception? parseFailure = null;
        try
        {
            await FastSqlEngine.Default.AnalyzeAsync("SELECT FROM WHERE".AsMemory(), null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            parseFailure = ex;
        }

        parseFailure.ShouldNotBeNull();
        var json = Mapped(parseFailure);
        McpErrorEnvelopeAssert.Code(json).ShouldBe("INVALID_QUERY");
        json.ShouldNotContain("FROM WHERE");
    }

    [Fact]
    public void QueryDataset_ArgumentException_InvalidQuery()
    {
        var json = Mapped(new ArgumentException("The property 'ssn' does not exist or is not accessible."));

        McpErrorEnvelopeAssert.Code(json).ShouldBe("INVALID_QUERY");
        McpErrorEnvelopeAssert.Message(json).ShouldBe("The query parameters are invalid.");
        json.ShouldNotContain("ssn");
    }

    [Fact]
    public void QueryDataset_RequiredFilterColumnMissing_HttpCode()
    {
        var json = Mapped(new RequiredFilterColumnMissingException("column region is required"));

        McpErrorEnvelopeAssert.Code(json).ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
        json.ShouldNotContain("region");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void QueryDataset_TableNotFoundAndForbidden_ByteIdentical_DatasetNotFound()
    {
        var results = new[]
        {
            Mapped(new TableNotFoundException(new Autheris.Domain.Common.TableIdentifier("sales", "public", "ghost"))),
            Mapped(new GatewayForbiddenException("policy detail")),
            Mapped(new GatewayUnauthorizedException("who are you")),
            Mapped(new UnauthorizedAccessException("denied by rule 7")),
            Mapped(new SecurityException("denied"))
        };

        McpErrorEnvelopeAssert.Code(results[0]).ShouldBe("DATASET_NOT_FOUND");
        results.ShouldAllBe(r => r == results[0]);
        results[0].ShouldNotContain("ghost");
    }

    [Fact]
    public void QueryDataset_TimeoutAndConcurrency_NotMappedHere()
    {
        foreach (var ex in new Exception[] { new OperationCanceledException(), new TimeoutException(), new ScopeConcurrencyExceededException(2), new TransportNotAllowedException("t", default) })
        {
            McpToolError.TryMapQueryDatasetFailure(ex, out var envelope).ShouldBeFalse(ex.GetType().Name);
            envelope.ShouldBeEmpty();
        }
    }

    [Fact]
    public void QueryDataset_UnknownException_NotMapped()
    {
        McpToolError.TryMapQueryDatasetFailure(new InvalidOperationException("password=p"), out _).ShouldBeFalse();
    }
}
