using System;
using System.Collections.Generic;

namespace Autheris.Domain.Model;

public enum DataSourceType
{
    Sql = 0,
    HttpDeclarative = 1,
    HttpPlugin = 2,
    LakehouseIceberg = 3,
    LakehouseDelta = 7
}

public enum HttpAuthMode
{
    None = 0,
    StaticApiKey = 1,
    ClientCredentials = 2,
    ForwardBearerToken = 3 // On-Behalf-Of
}

public enum HttpBatchType
{
    None = 0,
    QueryParameterList = 1, // ?ids=1,2,3
    JsonBodyArray = 2,      // POST /items/batch with ["1", "2"]
    ParallelSingleRequests = 3 // Concurrency-throttled fallback
}

public sealed class HttpEndpointDescriptor
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string PathTemplate { get; init; } = string.Empty; // e.g. "/api/v1/customers/{id}"
    public string Method { get; init; } = "GET";

    // Auth & Header Delegation (SEC M-6: Default to None to prevent arbitrary token forwarding to external specs)
    public HttpAuthMode AuthMode { get; init; } = HttpAuthMode.None;
    public string? ApiKeyHeaderName { get; init; }
    public string? ApiKeySecretName { get; init; }
    public IReadOnlyDictionary<string, string> ForwardHeaders { get; init; } = new Dictionary<string, string>();

    // Pushdown Rules
    public string? TenantIdQueryParam { get; init; } // e.g. "tenantId"
    public string? TenantIdHeaderName { get; init; } // e.g. "X-Tenant-Id"

    // Batching Configuration
    public HttpBatchType BatchType { get; init; } = HttpBatchType.ParallelSingleRequests;
    public string? BatchParamName { get; init; } // e.g. "ids"
    public int MaxConcurrentRequests { get; init; } = 10;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    // Response Mapping
    public string? JsonRootPath { get; init; } // e.g. "data.items" or null for root
    public string PrimaryKeyField { get; init; } = "id";
    public bool CompleteResponse { get; init; } = false;

    // Pagination Configuration (R-56)
    public HttpPaginationConfig? Pagination { get; init; }
}

public enum HttpPaginationStrategy
{
    None = 0,
    OffsetLimit = 1,     // ?offset={offset}&limit={limit}
    PageNumber = 2,      // ?page={page}&size={size}
    NextLinkUrl = 3,     // Body contains next page URL (e.g. @odata.nextLink)
    Cursor = 4           // ?cursor={next_cursor}
}

public sealed record HttpPaginationConfig(
    HttpPaginationStrategy Strategy = HttpPaginationStrategy.None,
    string? PageParamName = "page",
    string? SizeParamName = "limit",
    int DefaultPageSize = 100,
    int MaxPages = 50,
    string? NextCursorJsonPath = null,
    string? NextLinkJsonPath = null,
    bool ZeroIndexedPage = false,
    bool EnforceSameHost = true,
    int MaxStagedRows = 100_000,
    long MaxStagedBytes = 52_428_800);
