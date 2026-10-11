namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 CR-09: a paged read through the size-limiting handler (no declared body length, a non-seekable body stream)
/// still counts the staged bytes per table, without relying on <c>Stream.Length</c> of a non-seekable stream.
/// </summary>
public sealed class DeclarativeHttpPagedResponseTests
{
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PageHandler(int pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            int page = int.Parse(query["page"] ?? "1");
            var rows = page <= pages
                ? new[] { new { id = page * 2 - 1, name = new string('x', 200) }, new { id = page * 2, name = new string('y', 200) } }
                : [];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ChunkedContent(JsonSerializer.SerializeToUtf8Bytes(rows)) });
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(long maxStagedBytes)
    {
        var client = new HttpClient(new ResponseSizeLimitingHandler(10 * 1024 * 1024) { InnerHandler = new PageHandler(pages: 3) });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var executor = new DeclarativeHttpDataSourceExecutor(factory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, environment: env);
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.internal",
            PathTemplate = "/customers",
            CompleteResponse = true,
            Pagination = new HttpPaginationConfig(HttpPaginationStrategy.PageNumber, DefaultPageSize: 2, MaxStagedBytes: maxStagedBytes)
        };
        var id = new TableIdentifier("crm_api", "public", "customers");
        var metadata = new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "crm_api", SchemaName = "public", TableName = "customers", DataSourceType = DataSourceType.HttpDeclarative, HttpEndpoint = descriptor },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "varchar" }]
        };
        var context = new DataSourceExecutionContext(
            "crm_api", metadata, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Test")),
            TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true),
            new Dictionary<string, object?>(), ["id", "name"]);

        return await executor.ExecuteAsync(context);
    }

    [Fact]
    public async Task PagedRead_WithoutDeclaredLength_ReadsAllPagesWithinTheStagedByteCap()
    {
        (await ReadAsync(maxStagedBytes: 1024 * 1024)).Count.ShouldBe(6);
    }

    [Fact]
    public async Task PagedRead_WithoutDeclaredLength_StillEnforcesTheStagedByteCap()
    {
        // Three pages of about 450 bytes each exceed 900 bytes.
        await Should.ThrowAsync<ConnectorRowLimitExceededException>(() => ReadAsync(maxStagedBytes: 900));
    }
}
