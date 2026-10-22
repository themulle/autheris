namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Manifest model defining application metadata, domain ownership, and documentation entry points.
/// </summary>
public sealed record DocAppManifest(
    string AppId,
    string Name,
    string Version,
    string Domain,
    string Owner,
    string GovernanceTier,
    IReadOnlyDictionary<string, string>? EntryPoints,
    IReadOnlyList<string>? Tags
);

/// <summary>
/// Summary representation of a registered application in the universal doc lake.
/// </summary>
public sealed record DocAppSummary(
    string AppId,
    string Name,
    string Version,
    string Domain,
    string Owner,
    string GovernanceTier,
    int DocumentCount,
    IReadOnlyList<string> Tags
);

/// <summary>
/// Request payload to deposit a document or runbook via MCP or REST.
/// <see cref="TenantId"/> is the depositing tenant (null for official documentation deposited by a cluster administrator
/// or shipped with the gateway); <see cref="AuthorSid"/> is the depositing principal.
/// </summary>
public sealed record DepositDocRequest(
    string AppId,
    DocCategory Category,
    string Title,
    string Content,
    string? DocId = null,
    string? ErrorCode = null,
    IReadOnlyList<string>? Tags = null,
    string? TenantId = null,
    string? AuthorSid = null
);

/// <summary>
/// Result of a documentation deposit operation.
/// </summary>
public sealed record DepositDocResult(
    bool Success,
    DocChunkIdentifier? ChunkId,
    string? FilePath,
    string Message,
    string? ErrorCode = null
);
