namespace Autheris.Tests.Unit.Mcp;

/// <summary>Golden texts of the dispatcher documentation as shipped before PLAN-SEC-115 (AC 4: defaults stay byte-identical).</summary>
internal static class ApiDispatcherGolden
{
    public const string EndpointsDocumentation = """
        # Autheris Enterprise Gateway - REST API Documentation

        ## 1. Governed REST Data API
        - **GET /api/v1/data/{domain}/{schema}/{table}**
          - Description: Queries dataset records under tenant isolation, row filters and column masking.
          - Query parameters:
            - `select` (string): Comma-separated columns (e.g. `id,amount,status`).
            - `filter` (string): Safe AST predicate (e.g. `status eq 'active' and amount gt 100`).
            - `orderBy` (string): Sorting expression (e.g. `createdDate desc`).
            - `limit` (int): Page size (default 50, max 1000).
            - `offset` (int): Offset paging.
          - Returns: `DatasetQueryEnvelope` with metadata and rows.

        ## 2. Catalog & Discovery API
        - **GET /api/v1/catalog/datasets**: Lists all datasets visible to the caller (ReBAC filtered).
        - **GET /api/v1/catalog/datasets/{datasetId}**: Returns detailed column schema and masking states.
        - **GET /api/v1/catalog/datasources**: Lists registered datasources without leaking secrets.
        - **GET /api/v1/catalog/search?q={query}&domain={domain}**: Fulltext search over datasets and columns.

        ## 3. WebSQL & Statement Execution
        - **POST /api/v1/statement**
          - Description: Executes governed SQL statements with automatic AST rewriting.
          - Request body: `{ "sql": "SELECT id, email FROM sales.public.customers" }`.

        ## 4. Governance & Access Self-Service
        - **GET /api/v1/governance/me/access**: Transparent view of current identity, roles, and access boundaries.
        """;

    public const string StaticOpenApi = """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "Autheris Enterprise Governance Gateway API",
            "version": "2.0.0",
            "description": "Governed REST Data API, Catalog Discovery, and WebSQL execution."
          },
          "paths": {
            "/api/v1/data/{domain}/{table}": {
              "get": {
                "summary": "Governed REST Data API",
                "responses": { "200": { "description": "Dataset query envelope" } }
              }
            },
            "/api/v1/catalog/datasets": {
              "get": {
                "summary": "List catalog datasets",
                "responses": { "200": { "description": "Datasets list" } }
              }
            },
            "/api/v1/catalog/datasources": {
              "get": {
                "summary": "List datasources",
                "responses": { "200": { "description": "Datasources list" } }
              }
            },
            "/api/v1/statement": {
              "post": {
                "summary": "Execute WebSQL statement",
                "responses": { "200": { "description": "Governed SQL query result" } }
              }
            }
          }
        }
        """;
}
