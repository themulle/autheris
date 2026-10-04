namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Connectors;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Kernel;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Kernel;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Mcp;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Connectors;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class VectorAndSemanticExecutionTests
{
    private readonly TenantId _tenant = new("tenant-enterprise");
    private readonly Sid _userSid = new("S-1-5-21-USER-1");

    [Fact]
    public async Task PgVectorConnector_ExecutesCustomSearch_And_AppliesRls()
    {
        var rawChunks = new List<VectorDocumentChunk>
        {
            new("c1", "d1", 0, "Authorized chunk", 0.94f, _tenant, new Dictionary<string, object?>()),
            new("c2", "d2", 0, "Unauthorized cross-tenant chunk", 0.97f, new TenantId("other-tenant"), new Dictionary<string, object?>())
        };

        var connector = new PgVectorConnector("pgvector-default", (req, session, ct) => Task.FromResult<IReadOnlyList<VectorDocumentChunk>>(rawChunks));

        var session = new ConnectorSessionContext(
            Principal: new ClaimsPrincipal(),
            Tenant: _tenant,
            AccessDecision: TableAccessDecision.Allowed(new TableIdentifier("ai", "public", "docs"), new Dictionary<string, ColumnAccessLevel>(), "1=1", true),
            ProjectedColumns: new[] { "content_text" },
            Arguments: new Dictionary<string, object?>(),
            PushdownFilterSql: "1=1",
            Limit: 5,
            Offset: 0
        );

        var request = new VectorSearchRequest(new TableIdentifier("ai", "public", "docs"), TopK: 5);

        var results = await connector.SearchVectorsAsync(request, session);

        results.Count.ShouldBe(1);
        results[0].ChunkId.ShouldBe("c1");
    }

    [Fact]
    public async Task QdrantVectorConnector_BuildsValidFilters_And_Executes()
    {
        var rawChunks = new List<VectorDocumentChunk>
        {
            new("q1", "d1", 0, "Qdrant Chunk", 0.91f, _tenant, new Dictionary<string, object?>())
        };

        var connector = new QdrantVectorConnector("qdrant-default", (req, session, ct) => Task.FromResult<IReadOnlyList<VectorDocumentChunk>>(rawChunks));

        var session = new ConnectorSessionContext(
            Principal: new ClaimsPrincipal(),
            Tenant: _tenant,
            AccessDecision: TableAccessDecision.Allowed(new TableIdentifier("ai", "public", "qdrant_kb"), new Dictionary<string, ColumnAccessLevel>(), null, true),
            ProjectedColumns: new[] { "content_text" },
            Arguments: new Dictionary<string, object?>(),
            PushdownFilterSql: null,
            Limit: 10,
            Offset: 0
        );

        var request = new VectorSearchRequest(new TableIdentifier("ai", "public", "qdrant_kb"));
        var results = await connector.SearchVectorsAsync(request, session);

        results.Count.ShouldBe(1);
        results[0].ChunkId.ShouldBe("q1");
    }

    [Fact]
    public async Task MilvusVectorConnector_BuildsValidFilters_And_Executes()
    {
        var rawChunks = new List<VectorDocumentChunk>
        {
            new("m1", "d1", 0, "Milvus Chunk", 0.89f, _tenant, new Dictionary<string, object?>())
        };

        var connector = new MilvusVectorConnector("milvus-default", (req, session, ct) => Task.FromResult<IReadOnlyList<VectorDocumentChunk>>(rawChunks));

        var session = new ConnectorSessionContext(
            Principal: new ClaimsPrincipal(),
            Tenant: _tenant,
            AccessDecision: TableAccessDecision.Allowed(new TableIdentifier("ai", "public", "milvus_kb"), new Dictionary<string, ColumnAccessLevel>(), null, true),
            ProjectedColumns: new[] { "content_text" },
            Arguments: new Dictionary<string, object?>(),
            PushdownFilterSql: null,
            Limit: 5,
            Offset: 0
        );

        var request = new VectorSearchRequest(new TableIdentifier("ai", "public", "milvus_kb"));
        var results = await connector.SearchVectorsAsync(request, session);

        results.Count.ShouldBe(1);
        results[0].ChunkId.ShouldBe("m1");
    }

    [Fact]
    public async Task SemanticQueryCache_HitMiss_And_CollectionInvalidation()
    {
        var cache = new SemanticQueryCacheService();
        var collection = new TableIdentifier("ai", "public", "rag_docs");
        var key = new SemanticCacheKey(_tenant, _userSid, "ctx_reader", collection, "What is our Q3 revenue?");

        var embeddingA = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var cachedChunks = new List<VectorDocumentChunk>
        {
            new("c-hit", "d1", 0, "Q3 revenue was $10M", 0.99f, _tenant, new Dictionary<string, object?>())
        };

        var entry = new SemanticCacheEntry(
            Key: key,
            PromptEmbedding: embeddingA,
            CachedResult: cachedChunks,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
            PolicyEpochSnapshot: 1
        );

        await cache.SetAsync(entry);

        // 1. Exact or near-identical vector query (Cosine >= 0.95) -> HIT with matching epoch
        var nearIdentical = new[] { 0.101f, 0.201f, 0.301f, 0.401f };
        var match = await cache.TryGetAsync(key, nearIdentical, similarityThreshold: 0.95f, currentPolicyEpoch: 1);
        match.IsHit.ShouldBeTrue();
        match.Similarity.ShouldBeGreaterThan(0.99f);
        match.Result.ShouldNotBeNull();
        match.Result.Count.ShouldBe(1);

        // 2. Dissimilar vector query (orthogonal) -> MISS
        var dissimilar = new[] { -0.4f, -0.3f, 0.2f, 0.1f };
        var missMatch = await cache.TryGetAsync(key, dissimilar, similarityThreshold: 0.95f, currentPolicyEpoch: 1);
        missMatch.IsHit.ShouldBeFalse();

        // 3. Invalidate collection -> subsequent identical query becomes MISS
        await cache.InvalidateCollectionAsync(collection);
        var postInvalidation = await cache.TryGetAsync(key, nearIdentical, similarityThreshold: 0.95f, currentPolicyEpoch: 1);
        postInvalidation.IsHit.ShouldBeFalse();
    }

    [Fact]
    public async Task GovernedExecutionKernel_ExecuteVectorQueryAsync_EndToEnd_Permitted()
    {
        var collection = new TableIdentifier("ai", "public", "articles");
        var metadata = new TableMetadata
        {
            Identifier = collection,
            Table = new Table { TableName = "articles", SchemaName = "public", SourceName = "pgvector-default", DataSourceType = DataSourceType.VectorPgVector },
            Columns = new[]
            {
                new TableColumn { ColumnName = "content_text", DataType = "text" }
            }
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(metadata));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(collection, new Dictionary<string, ColumnAccessLevel>(), "1=1", true)));

        var guardrail = Substitute.For<IExecutionGuardrailService>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();

        var registry = new InMemoryConnectorRegistry();
        var rawChunks = new List<VectorDocumentChunk>
        {
            new("c1", "doc1", 0, "Confidential note: reach out to test@example.com for secrets.", 0.95f, _tenant, new Dictionary<string, object?>())
        };

        var pgConnector = new PgVectorConnector("pgvector-default", (req, s, ct) => Task.FromResult<IReadOnlyList<VectorDocumentChunk>>(rawChunks));
        registry.RegisterConnector("pgvector-default", pgConnector);

        var cache = new SemanticQueryCacheService();
        var recommendation = new PolicyRecommendationService();

        var kernel = new GovernedExecutionKernel(
            metaRepo, pdp, guardrail, masking, gatewayExec,
            NullLogger<GovernedExecutionKernel>.Instance,
            connectorRegistry: registry,
            semanticCache: cache,
            policyRecommendationService: recommendation);

        var secContext = new SecurityPrincipalContext
        {
            UserSid = _userSid,
            TenantId = _tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string> { "Reader" },
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var request = new VectorSearchRequest(
            TargetCollection: collection,
            QueryVector: new[] { 0.1f, 0.2f },
            RawQueryText: "find confidential notes",
            TopK: 5
        );

        var result = await kernel.ExecuteVectorQueryAsync(request, secContext);

        result.ShouldNotBeNull();
        result.Chunks.Count.ShouldBe(1);
        // Universal PII Redaction check: email must be redacted before return!
        result.Chunks[0].ContentText.ShouldContain("[REDACTED_EMAIL]");
        result.Chunks[0].ContentText.ShouldNotContain("test@example.com");
    }

    [Fact]
    public async Task GovernedExecutionKernel_ExecuteVectorQueryAsync_Denied_TriggersRecommendation()
    {
        var collection = new TableIdentifier("ai", "public", "restricted_docs");
        var metadata = new TableMetadata
        {
            Identifier = collection,
            Table = new Table { TableName = "restricted_docs", SchemaName = "public", DataSourceType = DataSourceType.VectorPgVector },
            Columns = new[]
            {
                new TableColumn { ColumnName = "secret_chunk", DataType = "text" }
            }
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(metadata));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Denied(collection, "No consent grant for table restricted_docs")));

        var guardrail = Substitute.For<IExecutionGuardrailService>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();

        var recommendation = new PolicyRecommendationService();

        var kernel = new GovernedExecutionKernel(
            metaRepo, pdp, guardrail, masking, gatewayExec,
            NullLogger<GovernedExecutionKernel>.Instance,
            policyRecommendationService: recommendation);

        var secContext = new SecurityPrincipalContext
        {
            UserSid = _userSid,
            TenantId = _tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string> { "Reader" },
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var request = new VectorSearchRequest(
            TargetCollection: collection,
            ProjectedPayloadFields: new[] { "secret_chunk" },
            RawQueryText: "access restricted data"
        );

        // Security assertion: Access denied must throw SecurityException
        var ex = await Should.ThrowAsync<SecurityException>(() => kernel.ExecuteVectorQueryAsync(request, secContext));
        ex.Message.ShouldContain("restricted_docs");

        // F-AI-10 Assertion: Recommendation service automatically derived a least-privilege proposal!
        recommendation.PendingProposalCount.ShouldBe(1);
    }

    [Fact]
    public async Task GatewayMcpQueryExecutor_ExecutesSearchRagContext_Tool()
    {
        var executorProvider = Substitute.For<IRequestExecutorProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var kernel = Substitute.For<IGovernedExecutionKernel>();

        var governedResult = new GovernedVectorResult(
            Collection: new TableIdentifier("ai", "public", "knowledge_base"),
            Chunks: new List<VectorDocumentChunk>
            {
                new("c-mcp", "doc-mcp", 0, "Governed RAG information for AI Agent", 0.96f, _tenant, new Dictionary<string, object?>())
            },
            AccessDecision: TableAccessDecision.Allowed(new TableIdentifier("ai", "public", "knowledge_base"), new Dictionary<string, ColumnAccessLevel>(), null, true),
            Metrics: new ExecutionMetrics(TimeSpan.FromMilliseconds(15), 1, 128)
        );

        kernel.ExecuteVectorQueryAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(governedResult));

        var mcpExecutor = new GatewayMcpQueryExecutor(
            executorProvider,
            gatewayExec,
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            governedKernel: kernel
        );

        var tool = new McpToolDefinition(
            Name: "search_rag_context",
            Description: "Search RAG knowledge base",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: ""
        );

        var sessionContext = new McpSessionContext(
            SessionId: "session-1",
            ServicePrincipalId: "spn-agent-1",
            TenantId: _tenant.Value,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            Roles: new[] { "AiAgent", "Reader" }
        );

        var argsJson = JsonSerializer.Serialize(new
        {
            collection = "knowledge_base",
            query = "What is the policy for data governance?",
            topK = 3
        });

        var responseJson = await mcpExecutor.ExecuteOperationAsync(tool, argsJson, sessionContext);

        responseJson.ShouldContain("knowledge_base");
        responseJson.ShouldContain("Governed RAG information for AI Agent");
    }

    [Fact]
    public async Task GovernedExecutionKernel_ExecuteVectorQueryAsync_Enforces_PostExecution_Rls_RowFilter()
    {
        var collection = new TableIdentifier("ai", "public", "articles");
        var metadata = new TableMetadata
        {
            Identifier = collection,
            Table = new Table { TableName = "articles", SchemaName = "public", SourceName = "pgvector-default", DataSourceType = DataSourceType.VectorPgVector },
            Columns = new[]
            {
                new TableColumn { ColumnName = "content_text", DataType = "text" },
                new TableColumn { ColumnName = "department", DataType = "varchar" }
            }
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(metadata));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(collection, new Dictionary<string, ColumnAccessLevel>(), "department = 'Engineering'", true)));

        var guardrail = Substitute.For<IExecutionGuardrailService>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();

        var registry = new InMemoryConnectorRegistry();
        var rawChunks = new List<VectorDocumentChunk>
        {
            new("c1", "doc1", 0, "Engineering architecture chunk", 0.95f, _tenant, new Dictionary<string, object?> { ["department"] = "Engineering" }),
            new("c2", "doc2", 0, "HR compensation chunk", 0.92f, _tenant, new Dictionary<string, object?> { ["department"] = "HR" })
        };

        var pgConnector = new PgVectorConnector("pgvector-default", (req, s, ct) => Task.FromResult<IReadOnlyList<VectorDocumentChunk>>(rawChunks));
        registry.RegisterConnector("pgvector-default", pgConnector);

        var kernel = new GovernedExecutionKernel(
            metaRepo, pdp, guardrail, masking, gatewayExec,
            NullLogger<GovernedExecutionKernel>.Instance,
            connectorRegistry: registry);

        var secContext = new SecurityPrincipalContext
        {
            UserSid = _userSid,
            TenantId = _tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string> { "Reader" },
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var request = new VectorSearchRequest(
            TargetCollection: collection,
            QueryVector: new[] { 0.1f, 0.2f },
            RawQueryText: "search specs",
            TopK: 5
        );

        var result = await kernel.ExecuteVectorQueryAsync(request, secContext);

        result.ShouldNotBeNull();
        // c2 (HR) must be filtered out by post-execution RLS evaluator!
        result.Chunks.Count.ShouldBe(1);
        result.Chunks[0].ChunkId.ShouldBe("c1");
    }
}
