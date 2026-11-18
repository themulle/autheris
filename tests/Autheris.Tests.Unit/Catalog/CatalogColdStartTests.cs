namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogColdStartTests
{
    private static CatalogSnapshotProvider CreateProvider(Func<CatalogVersion> version, IReadOnlyList<TableMetadata>? tables = null)
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(tables ?? []);
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));
        var versions = Substitute.For<ICatalogVersionProvider>();
        versions.GetCatalogVersionAsync(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<CatalogVersion>(version()));
        return new CatalogSnapshotProvider(repo, versions);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task BeforeSnapshotLoaded_ReturnsUnavailableNotEmpty()
    {
        await using var provider = CreateProvider(() => throw new InvalidOperationException("governance store down"));

        provider.IsInitialized.ShouldBeFalse();
        await Should.ThrowAsync<CatalogSnapshotNotReadyException>(async () => await provider.GetSnapshotAsync());
        provider.IsInitialized.ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task LoadedEmptyCatalog_IsAValidEmptySnapshot_NotUnavailable()
    {
        await using var provider = CreateProvider(() => new CatalogVersion(1, 1));

        var snapshot = await provider.GetSnapshotAsync();

        provider.IsInitialized.ShouldBeTrue();
        snapshot.TableCount.ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SnapshotNotReady_IsMappedTo503WithRetryAfter()
    {
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, new CatalogSnapshotNotReadyException(), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().ShouldNotBeNullOrEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SearchIndexNotReady_IsMappedTo503WithRetryAfter()
    {
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, new CatalogSearchIndexNotReadyException(), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }
}
