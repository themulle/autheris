namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>Status of a version of an app-scoped trusted document set (Issue #281). A retired version stays stored but never authorises a document.</summary>
public static class TrustedDocumentVersionStatus
{
    public const string Active = "active";
    public const string Retired = "retired";
}

/// <summary>One active version of an app with its exact-text document hashes and the origins of the owning app (Issue #281).</summary>
public sealed record TrustedDocumentVersionRecord(string AppId, string Version, IReadOnlyList<string> Hashes, IReadOnlyList<string> Origins);

/// <summary>The active state read in one transaction together with the generation it belongs to (Issue #281).</summary>
public sealed record TrustedDocumentStoreSnapshot(long Generation, IReadOnlyList<TrustedDocumentVersionRecord> ActiveVersions);

/// <summary>A version of an app for the admin read model (no hashes, only their count).</summary>
public sealed record TrustedDocumentVersionSummary(
    string Version, string Status, int HashCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RetiredAt);

/// <summary>An app with its origins and all versions (active and retired) for the admin read model.</summary>
public sealed record TrustedDocumentAppView(string AppId, IReadOnlyList<string> Origins, IReadOnlyList<TrustedDocumentVersionSummary> Versions);

/// <summary>What a replace of a version set changed. Hashes are sorted ordinally.</summary>
public sealed record TrustedDocumentSetDelta(
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed, int Unchanged, bool Created, bool OriginsChanged);

/// <summary>Result of retiring a version.</summary>
public enum TrustedDocumentRetireOutcome
{
    NotFound = 0,
    Retired = 1,
    AlreadyRetired = 2
}

/// <summary>A PUT targeted a retired version; retired versions are never reactivated (OQ-4).</summary>
public sealed class TrustedDocumentVersionRetiredException : InvalidOperationException
{
    public TrustedDocumentVersionRetiredException() : base("The trusted document version is retired.")
    {
    }
}

/// <summary>A PUT would create an active version beyond the configured cap of active versions per app (SEC-281-8).</summary>
public sealed class TrustedDocumentVersionLimitException : InvalidOperationException
{
    public TrustedDocumentVersionLimitException() : base("The app already has the maximum number of active versions.")
    {
    }
}
