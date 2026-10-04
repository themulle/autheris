namespace Autheris.Application.Kernel;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Connectors;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Kernel;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

/// <summary>
/// Architecture Phase 2 (WP 2.4): Governed Data Pipeline Kernel.
/// Enforces canonical normalization, unified PDP evaluation, resource guardrails, and post-execution masking.
/// </summary>
public sealed class GovernedExecutionKernel : IGovernedExecutionKernel
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly IExecutionGuardrailService _guardrailService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IGatewayExecutionService _gatewayExecutionService;
    private readonly ILogger<GovernedExecutionKernel> _logger;
    private readonly IAutherisConnectorRegistry? _connectorRegistry;
    private readonly ISemanticQueryCache? _semanticCache;
    private readonly PolicyRecommendationService? _policyRecommendationService;
    private readonly IConsentCacheService? _consentCacheService;
    private readonly IEmbeddingGenerator? _embeddingGenerator;

    public GovernedExecutionKernel(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IExecutionGuardrailService guardrailService,
        IColumnMaskingProvider maskingProvider,
        IGatewayExecutionService gatewayExecutionService,
        ILogger<GovernedExecutionKernel> logger,
        IAutherisConnectorRegistry? connectorRegistry = null,
        ISemanticQueryCache? semanticCache = null,
        PolicyRecommendationService? policyRecommendationService = null,
        IConsentCacheService? consentCacheService = null,
        IEmbeddingGenerator? embeddingGenerator = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _guardrailService = guardrailService ?? throw new ArgumentNullException(nameof(guardrailService));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectorRegistry = connectorRegistry;
        _semanticCache = semanticCache;
        _policyRecommendationService = policyRecommendationService;
        _consentCacheService = consentCacheService;
        _embeddingGenerator = embeddingGenerator;
    }

    public async Task<GovernedExecutionResult> ExecuteAsync(
        GovernedExecutionRequest request,
        SecurityPrincipalContext securityContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(securityContext);

        var stopwatch = Stopwatch.StartNew();

        // 1. Canonical Normalization (WP 2.1)
        var normalizedTable = TableIdentifierNormalizer.Normalize(
            request.TargetTable.ToString(),
            request.TargetTable.Domain,
            request.TargetTable.Schema);

        // 2. Metadata Catalog Validation (RR-L3-06)
        var metadata = await _metadataRepository.GetTableMetadataAsync(normalizedTable, ct).ConfigureAwait(false);
        if (metadata == null)
        {
            var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
            metadata = allTables.FirstOrDefault(t =>
                t.Identifier.Equals(normalizedTable) ||
                string.Equals(t.Identifier.TableName, normalizedTable.TableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Identifier.ToQualifiedName(), normalizedTable.ToQualifiedName(), StringComparison.OrdinalIgnoreCase));
        }

        if (metadata == null)
        {
            _logger.LogWarning("Execution rejected: Table {Table} not found in catalog.", normalizedTable);
            throw new TableNotFoundException(normalizedTable);
        }

        // 3. Execution Guardrails (WP 2.3)
        _guardrailService.ValidateSingleStatement(request.SqlPredicate);
        var effectiveLimit = _guardrailService.EnforceRowLimit(request.RequestedLimit);

        // 4. Unified PDP Evaluation (WP 2.2)
        var decision = await _pdp.EvaluateAccessAsync(
            metadata.Identifier,
            metadata,
            securityContext,
            request.RequestedColumns,
            ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            var reason = string.Join("; ", decision.DeniedReasons);
            _logger.LogWarning(
                "Access to table {Table} denied for subject {Subject}: {Reason}",
                metadata.Identifier, securityContext.UserSid.Value, reason);

            throw new SecurityException($"Access to table '{metadata.Identifier}' was rejected: {reason}");
        }

        // 5. Build Principal & Execute Query
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, securityContext.UserSid.Value),
            new("tenant_id", securityContext.TenantId.Value)
        };
        foreach (var role in securityContext.TenantRoles.Concat(securityContext.ClusterRoles))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        foreach (var grp in securityContext.GroupSids)
        {
            claims.Add(new Claim("groupsid", grp.Value));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, securityContext.AuthenticationScheme));

        var (rawRows, executionDecision) = await _gatewayExecutionService.ExecuteTableQueryAsync(
            principal,
            metadata.Identifier,
            first: effectiveLimit,
            after: null,
            queryArguments: null,
            requestedFields: request.RequestedColumns,
            requestHeaders: null,
            ct: ct).ConfigureAwait(false);

        // 6. Universal Post-Execution Masking (WP 2.4, RR-L6-01, RR-L6-03)
        var effectiveDecision = executionDecision ?? decision;
        var maskedRows = new List<IReadOnlyDictionary<string, object?>>(rawRows.Count);

        foreach (var row in rawRows)
        {
            var maskedRow = new Dictionary<string, object?>(row.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (colName, val) in row)
            {
                var accessLevel = effectiveDecision.GetEffectiveColumnAccess(colName, metadata);
                if (accessLevel == ColumnAccessLevel.Deny)
                {
                    // Omit or nullify denied columns
                    continue;
                }

                if (accessLevel == ColumnAccessLevel.Mask)
                {
                    var rule = metadata.ColumnMaskingRules.TryGetValue(colName, out var foundRule)
                        ? foundRule
                        : new MaskingRule { RuleType = "REDACT" };

                    maskedRow[colName] = _maskingProvider.MaskValue(colName, val, rule);
                }
                else
                {
                    maskedRow[colName] = val;
                }
            }
            maskedRows.Add(maskedRow);
        }

        stopwatch.Stop();

        var projectedColumns = maskedRows.Count > 0
            ? maskedRows[0].Keys.ToList()
            : (request.RequestedColumns ?? metadata.Columns.Select(c => c.ColumnName).ToList());

        var metrics = new ExecutionMetrics(
            Duration: stopwatch.Elapsed,
            RowCount: maskedRows.Count,
            EstimatedBytes: maskedRows.Count * 64 // Approximate
        );

        return new GovernedExecutionResult(
            NormalizedTable: metadata.Identifier,
            Columns: projectedColumns,
            Rows: maskedRows,
            AccessDecision: effectiveDecision,
            Metrics: metrics
        );
    }

    public async Task<GovernedVectorResult> ExecuteVectorQueryAsync(
        VectorSearchRequest request,
        SecurityPrincipalContext securityContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(securityContext);

        var stopwatch = Stopwatch.StartNew();

        // 1. Canonical Normalization
        var normalizedTable = TableIdentifierNormalizer.Normalize(
            request.TargetCollection.ToString(),
            request.TargetCollection.Domain,
            request.TargetCollection.Schema);

        // 2. Metadata Catalog Validation
        var metadata = await _metadataRepository.GetTableMetadataAsync(normalizedTable, ct).ConfigureAwait(false);
        if (metadata == null)
        {
            var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
            metadata = allTables.FirstOrDefault(t =>
                t.Identifier.Equals(normalizedTable) ||
                string.Equals(t.Identifier.TableName, normalizedTable.TableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Identifier.ToQualifiedName(), normalizedTable.ToQualifiedName(), StringComparison.OrdinalIgnoreCase));
        }

        if (metadata == null)
        {
            _logger.LogWarning("Vector execution rejected: Collection {Collection} not found in catalog.", normalizedTable);
            throw new TableNotFoundException(normalizedTable);
        }

        // Zero-Trust: Ensure target table is explicitly a governed vector source
        if (metadata.DataSourceType is not (DataSourceType.VectorPgVector or DataSourceType.VectorQdrant or DataSourceType.VectorMilvus))
        {
            _logger.LogWarning(
                "Vector execution rejected: Table {Collection} has DataSourceType {Type}, not a governed vector source.",
                normalizedTable, metadata.DataSourceType);
            throw new SecurityException($"Target table '{normalizedTable}' is not a vector collection.");
        }

        // Enforce consistent global TopK bound
        request = request with { TopK = Math.Clamp(request.TopK, 1, 200) };

        // 3. Unified PDP Evaluation
        var decision = await _pdp.EvaluateAccessAsync(
            metadata.Identifier,
            metadata,
            securityContext,
            request.ProjectedPayloadFields,
            ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            var reason = string.Join("; ", decision.DeniedReasons);
            _logger.LogWarning(
                "Access to vector collection {Collection} denied for subject {Subject}: {Reason}",
                metadata.Identifier, securityContext.UserSid.Value, reason);

            // Hook for F-AI-10: Trigger Least-Privilege Policy Recommendation
            if (_policyRecommendationService != null)
            {
                var denialEvent = new AccessDenialEvent(
                    EventId: Guid.NewGuid(),
                    Timestamp: DateTimeOffset.UtcNow,
                    TenantId: securityContext.TenantId,
                    RequesterSid: securityContext.UserSid,
                    Roles: new HashSet<string>(securityContext.TenantRoles.Concat(securityContext.ClusterRoles)),
                    TargetTable: metadata.Identifier,
                    RequestedColumns: request.ProjectedPayloadFields ?? metadata.Columns.Select(c => c.ColumnName).ToList(),
                    DenialReason: reason,
                    IntendedPurpose: request.RawQueryText
                );
                _policyRecommendationService.GenerateProposal(denialEvent, metadata.Columns.Select(c => c.ColumnName).ToList());
            }

            throw new SecurityException($"Access to vector collection '{metadata.Identifier}' was rejected: {reason}");
        }

        // 3.5 Generate embedding if missing and embedding generator is configured
        if (request.QueryVector == null && _embeddingGenerator != null && !string.IsNullOrWhiteSpace(request.RawQueryText))
        {
            var genVector = await _embeddingGenerator.GenerateEmbeddingAsync(request.RawQueryText, ct).ConfigureAwait(false);
            request = request with { QueryVector = genVector };
        }

        // 4. Zero-Trust Semantic Cache Check (F-AI-10)
        var currentEpoch = _consentCacheService != null
            ? (await _consentCacheService.GetEpochSnapshotAsync(metadata.Identifier, ct).ConfigureAwait(false)) ?? 1L
            : 1L;

        var contextHash = IConsentCacheService.ComputeSubjectContextHash(
            securityContext.GroupSids,
            new HashSet<string>(securityContext.TenantRoles.Concat(securityContext.ClusterRoles)));

        if (_semanticCache != null && request.QueryVector != null && !string.IsNullOrWhiteSpace(request.RawQueryText))
        {
            var cacheKey = new SemanticCacheKey(
                TenantId: securityContext.TenantId,
                UserSid: securityContext.UserSid,
                SecurityContextHash: contextHash,
                Collection: metadata.Identifier,
                NormalizedPrompt: request.RawQueryText
            );

            var cacheMatch = await _semanticCache.TryGetAsync(
                cacheKey,
                request.QueryVector.ToArray(),
                request.MinSimilarityScore,
                currentEpoch,
                ct).ConfigureAwait(false);

            if (cacheMatch.IsHit && cacheMatch.Result != null)
            {
                stopwatch.Stop();
                var redactedCached = cacheMatch.Result.Select(c => ChunkPiiRedactor.RedactChunk(c, metadata, _maskingProvider)).ToList();
                return new GovernedVectorResult(
                    Collection: metadata.Identifier,
                    Chunks: redactedCached,
                    AccessDecision: decision,
                    Metrics: new ExecutionMetrics(stopwatch.Elapsed, redactedCached.Count, redactedCached.Count * 128)
                );
            }
        }

        // 5. Connector Resolution & Execution (F-AI-09)
        IReadOnlyList<VectorDocumentChunk> rawChunks = Array.Empty<VectorDocumentChunk>();
        if (_connectorRegistry != null)
        {
            var connectorName = metadata.Table.SourceName;
            if (string.IsNullOrWhiteSpace(connectorName))
            {
                connectorName = metadata.DataSourceType switch
                {
                    DataSourceType.VectorPgVector => "pgvector-default",
                    DataSourceType.VectorQdrant => "qdrant-default",
                    DataSourceType.VectorMilvus => "milvus-default",
                    _ => "default"
                };
            }

            var connector = _connectorRegistry.GetConnector(connectorName);
            if (connector is IVectorAutherisConnector vectorConnector)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, securityContext.UserSid.Value),
                    new("tenant_id", securityContext.TenantId.Value)
                };
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, securityContext.AuthenticationScheme));

                var session = new ConnectorSessionContext(
                    Principal: principal,
                    Tenant: securityContext.TenantId,
                    AccessDecision: decision,
                    ProjectedColumns: request.ProjectedPayloadFields ?? metadata.Columns.Select(c => c.ColumnName).ToList(),
                    Arguments: new Dictionary<string, object?>(),
                    PushdownFilterSql: decision.CombinedRowFilterSql,
                    Limit: request.TopK,
                    Offset: 0
                );

                rawChunks = await vectorConnector.VectorRecordSource.SearchVectorsAsync(request, session, ct).ConfigureAwait(false);
            }
        }

        // 6. Zero-Trust Post-Execution Chunk RLS Filter (INV-VEC-03)
        var permittedChunks = VectorPushdownSecurityHelper.FilterChunksByRls(
            rawChunks,
            securityContext.TenantId,
            chunk =>
            {
                if (string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
                {
                    return true;
                }

                var payload = new Dictionary<string, object?>(chunk.Metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["tenant_id"] = chunk.TenantId.Value,
                    ["document_id"] = chunk.DocumentId,
                    ["chunk_id"] = chunk.ChunkId,
                    ["chunk_index"] = chunk.ChunkIndex,
                    ["content_text"] = chunk.ContentText
                };

                return StreamingRowFilterAstEvaluator.Matches(payload, decision.CombinedRowFilterSql);
            });

        // 7. Store in Semantic Cache
        if (_semanticCache != null && request.QueryVector != null && !string.IsNullOrWhiteSpace(request.RawQueryText))
        {
            var cacheKey = new SemanticCacheKey(
                TenantId: securityContext.TenantId,
                UserSid: securityContext.UserSid,
                SecurityContextHash: contextHash,
                Collection: metadata.Identifier,
                NormalizedPrompt: request.RawQueryText
            );

            var entry = new SemanticCacheEntry(
                Key: cacheKey,
                PromptEmbedding: request.QueryVector,
                CachedResult: permittedChunks,
                CreatedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                PolicyEpochSnapshot: currentEpoch
            );
            await _semanticCache.SetAsync(entry, ct).ConfigureAwait(false);
        }

        // 8. Universal PII Chunk Redaction (INV-SEC-01, INV-SEC-02)
        var sanitizedChunks = permittedChunks.Select(c => ChunkPiiRedactor.RedactChunk(c, metadata, _maskingProvider)).ToList();

        stopwatch.Stop();

        return new GovernedVectorResult(
            Collection: metadata.Identifier,
            Chunks: sanitizedChunks,
            AccessDecision: decision,
            Metrics: new ExecutionMetrics(stopwatch.Elapsed, sanitizedChunks.Count, sanitizedChunks.Count * 128)
        );
    }
}
