namespace Autheris.Tests.Unit.Governance;

using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Governance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07 review: the authorization of the ingestion route runs before tenant resolution and before the body is read.</summary>
public sealed class IngestOpenApiEndpointTests
{
    private sealed class ThrowingStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; throw new InvalidOperationException("body must not be read"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { WasRead = true; throw new InvalidOperationException("body must not be read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (DefaultHttpContext Context, ThrowingStream Body, IOpenApiIngestionService Ingestion) Create(string role, string? tenantClaim, string query = "")
    {
        var claims = new System.Collections.Generic.List<Claim> { new("user_sid", "S-1-5-21-PUB"), new(ClaimTypes.Role, role) };
        if (tenantClaim != null)
        {
            claims.Add(new Claim("tenant_id", tenantClaim));
        }

        var body = new ThrowingStream();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role))
        };
        context.Request.Body = body;
        context.Request.QueryString = new QueryString(query);
        return (context, body, Substitute.For<IOpenApiIngestionService>());
    }

    private static IOpenApiIngestionUseCase UseCase(IOpenApiIngestionService ingestion) =>
        new OpenApiIngestionUseCase(ingestion, Substitute.For<IAuditWriter>());

    [Fact]
    public async Task NonPublisher_Gets403_WithoutTheBodyBeingRead()
    {
        var (context, body, ingestion) = Create("StandardUser", "tenant-alpha");

        var result = await GovernanceEndpoints.IngestOpenApiAsync(context, UseCase(ingestion));

        result.ShouldBeOfType<StatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.WasRead.ShouldBeFalse();
    }

    [Fact]
    public async Task ForeignDomain_Gets403_WithoutTheBodyBeingRead()
    {
        var (context, body, ingestion) = Create("SchemaPublisher", "tenant-alpha", "?domain=tenant-bravo");

        var result = await GovernanceEndpoints.IngestOpenApiAsync(context, UseCase(ingestion));

        result.ShouldBeOfType<StatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.WasRead.ShouldBeFalse();
    }

    [Fact]
    public async Task NonPublisher_WithMalformedTenantClaim_Gets403_NotAnException()
    {
        var (context, body, ingestion) = Create("StandardUser", "bad tenant <script>");

        var result = await GovernanceEndpoints.IngestOpenApiAsync(context, UseCase(ingestion));

        result.ShouldBeOfType<StatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.WasRead.ShouldBeFalse();
    }
}
