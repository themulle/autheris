namespace Autheris.Tests.Unit.OData;

using System;
using Autheris.Tests.Common.Fakes;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.OData;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Befund 2.1 & 1.2: OData $filter pushdown with Zero-Trust security and Strict Content Negotiation.
/// </summary>
public sealed class ODataFilterPushdownTests
{
    private static readonly TableIdentifier Orders = new("sales", "main", "orders");

    private static TableMetadata OrderMetadata(DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "sales", SchemaName = "main", TableName = "orders", DataSourceType = type, SourceType = "PostgreSql" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "status", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "card_number", DataType = "text" }
        ]
    };

    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ColumnAccessLevel.Clear,
        ["status"] = ColumnAccessLevel.Clear,
        ["amount"] = ColumnAccessLevel.Clear,
        ["card_number"] = ColumnAccessLevel.Mask
    };

    // ------------------------------------------------------------------ Gateway

    private static (GatewayExecutionService Service, RecordingExecutor Executor) Gateway(DataSourceType type = DataSourceType.Sql)
    {
        var executor = new RecordingExecutor(type, 100);
        var metadataRepo = new InMemoryTableMetadataRepository();
        metadataRepo.Add(OrderMetadata(type));
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Orders, Access, hasUnconstrainedColumnAllow: true));
        var service = new GatewayExecutionService(
            metadataRepo,
            new InMemoryConsentRepository(),
            new InMemoryAuditLogRepository(),
            Substitute.For<IConsentResolutionService>(),
            cache,
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [executor]);
        return (service, executor);
    }

    private static ClaimsPrincipal User() => Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FILTER-USER"), new Claim("tenant_id", "sales")], "Test")));

    [Fact]
    public async Task Gateway_PassesFilterToExecutor_WhenColumnIsClear()
    {
        var (service, executor) = Gateway();
        var filter = ODataFilterParser.Parse("status eq 'completed'", DatabaseDialect.PostgreSql);

        await service.ExecuteTablePageAsync(User(), Orders,
            new TablePageRequest(First: 10, After: 0, Filter: filter));

        executor.Last.ShouldNotBeNull();
        executor.Last.Items.ContainsKey(TableQueryItems.Filter).ShouldBeTrue();
        executor.Last.Items[TableQueryItems.Filter].ShouldBe(filter);
    }

    [Fact]
    public async Task Gateway_FilteringOnMaskedColumn_ZeroTrust_ThrowsGatewayInvalidQueryException()
    {
        var (service, executor) = Gateway();
        var filter = ODataFilterParser.Parse("card_number eq '4111'", DatabaseDialect.PostgreSql);

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => service.ExecuteTablePageAsync(User(), Orders,
            new TablePageRequest(First: 10, After: 0, Filter: filter)));

        ex.Message.ShouldContain("The column 'card_number' in '$filter' does not exist or cannot be used for filtering.");
        executor.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Gateway_FilteringOnNonExistentColumn_ThrowsGatewayInvalidQueryException()
    {
        var (service, executor) = Gateway();
        var filter = ODataFilterParser.Parse("non_existent eq 'foo'", DatabaseDialect.PostgreSql);

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => service.ExecuteTablePageAsync(User(), Orders,
            new TablePageRequest(First: 10, After: 0, Filter: filter)));

        ex.Message.ShouldContain("The column 'non_existent' in '$filter' does not exist or cannot be used for filtering.");
        executor.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Gateway_NonSqlSource_ThrowsGatewayNotImplementedException()
    {
        var (service, _) = Gateway(DataSourceType.HttpDeclarative);
        var filter = ODataFilterParser.Parse("status eq 'completed'", DatabaseDialect.PostgreSql);

        await Should.ThrowAsync<GatewayNotImplementedException>(() => service.ExecuteTablePageAsync(User(), Orders,
            new TablePageRequest(First: 10, After: 0, Filter: filter)));
    }

    // ------------------------------------------------------------------ OData Handler & Endpoints

    [Fact]
    public async Task Handler_PassesFilterToGatewayExecutionService()
    {
        var execution = Substitute.For<IGatewayExecutionService>();
        execution.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TableQueryPage([], TableAccessDecision.Allowed(Orders, Access), 0));

        var metaRepo = new InMemoryTableMetadataRepository();
        metaRepo.Add(OrderMetadata());

        var handler = new ODataHandler(metaRepo, execution, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal: User(),
            serviceRootUrl: "https://gateway.example/odata/v4",
            table: Orders,
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null,
            orderBy: null,
            filter: "amount gt 100"
        );

        result.Success.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);

        await execution.Received(1).ExecuteTablePageAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Orders,
            Arg.Is<TablePageRequest>(r => r.Filter != null && r.Filter.ReferencedColumns.Contains("amount")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Endpoints_UnsupportedAcceptHeader_Returns406NotAcceptable()
    {
        var handler = Substitute.For<IODataHandler>();
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Headers.Accept = "text/csv";
        context.Response.Body = new MemoryStream();

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "main", "orders", handler, context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        await handler.DidNotReceiveWithAnyArgs().ExecuteEntitySetQueryAsync(default!, default!, default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task Endpoints_NdjsonAcceptHeader_Returns406NotAcceptable()
    {
        var handler = Substitute.For<IODataHandler>();
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Headers.Accept = "application/x-ndjson";
        context.Response.Body = new MemoryStream();

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "main", "orders", handler, context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        await handler.DidNotReceiveWithAnyArgs().ExecuteEntitySetQueryAsync(default!, default!, default!, default!, default!, default!, default!, default!);
    }

    private sealed class RecordingExecutor(DataSourceType type, long? totalCount) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => type;
        public DataSourceExecutionContext? Last { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            Last = context;
            if (context.Items.ContainsKey(TableQueryItems.CountTotal) && totalCount.HasValue)
            {
                context.Items[TableQueryItems.TotalCount] = totalCount.Value;
            }
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }
    }
}
