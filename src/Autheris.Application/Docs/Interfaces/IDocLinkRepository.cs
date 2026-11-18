namespace Autheris.Application.Docs.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Whether a link originates from a single document or from a whole application.</summary>
public enum DocLinkSourceKind { Document, Application }

/// <summary>Kind of catalog object a document or application can be linked to.</summary>
public enum DocLinkTargetKind { Table, View, Procedure, Column }

/// <summary>
/// Source of a link. <see cref="DocTenantId"/> is <c>null</c> for an official document or an application link;
/// <see cref="Category"/> and <see cref="DocId"/> are <c>null</c> for an application link.
/// </summary>
public sealed record DocLinkSource(DocLinkSourceKind Kind, string AppId, DocCategory? Category = null, string? DocId = null, string? DocTenantId = null)
{
    public static DocLinkSource ForDocument(DocKey key) => new(DocLinkSourceKind.Document, key.AppId, key.Category, key.DocId, key.TenantId);

    public static DocLinkSource ForApplication(string appId) => new(DocLinkSourceKind.Application, appId);
}

/// <summary>Catalog object addressed by a link. <see cref="Column"/> is required for <see cref="DocLinkTargetKind.Column"/>.</summary>
public sealed record DocLinkTarget(DocLinkTargetKind Kind, string Domain, string Schema, string Name, string? Column = null);

/// <summary>Persisted link row. <see cref="TenantId"/> is always the tenant of the target object.</summary>
public sealed record DocLinkRecord(
    string TenantId, Guid LinkId, DocLinkSource Source, DocLinkTarget Target, string CreatedBy, DateTimeOffset CreatedAtUtc);

/// <summary>Maximum number of targets one link replacement may carry.</summary>
public static class DocLinkLimits
{
    public const int MaxTargetsPerSource = 50;
}

/// <summary>Tenant-scoped store of links between documents/applications and catalog objects.</summary>
public interface IDocLinkRepository
{
    /// <summary>Lists the links of <paramref name="tenantId"/> that point at any of <paramref name="targets"/> (at most 50 targets).</summary>
    Task<IReadOnlyList<DocLinkRecord>> ListForTargetsAsync(string tenantId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default);

    /// <summary>Lists the links of <paramref name="tenantId"/> that originate from <paramref name="source"/>.</summary>
    Task<IReadOnlyList<DocLinkRecord>> ListForSourceAsync(string tenantId, DocLinkSource source, CancellationToken ct = default);

    /// <summary>Atomically replaces every link of <paramref name="source"/> in <paramref name="tenantId"/> with <paramref name="targets"/>.</summary>
    Task ReplaceForSourceAsync(string tenantId, DocLinkSource source, IReadOnlyList<DocLinkTarget> targets, string actor, CancellationToken ct = default);

    /// <summary>Deletes every link of <paramref name="source"/> in <paramref name="tenantId"/>; returns the number of removed links.</summary>
    Task<int> DeleteForSourceAsync(string tenantId, DocLinkSource source, CancellationToken ct = default);
}
