namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The lifecycle of a privilege in the provisioning ledger. A row is written as <see cref="Pending"/> before the privilege is
/// added in Unity Catalog, so the ledger always lists every privilege Autheris may have applied; it is deleted only after a
/// verified removal.
/// </summary>
public enum UcLedgerState
{
    /// <summary>Recorded before the add call; the privilege may or may not exist yet.</summary>
    Pending = 0,

    /// <summary>Added and confirmed by a read of the securable's permissions.</summary>
    Applied = 1,

    /// <summary>No consent wants the privilege any more; it is removed on the next successful call.</summary>
    RevokePending = 2,

    /// <summary>The add failed after the maximum number of attempts; it is retried at the longest backoff and reported as failed.</summary>
    Failed = 3
}

/// <summary>One privilege Autheris applied (or is about to apply) on a securable for a workspace principal.</summary>
public sealed record UcLedgerEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DataSource { get; init; }
    public required UcSecurableType SecurableType { get; init; }
    public required string FullName { get; init; }

    /// <summary>The Unity Catalog principal name the privilege was applied to.</summary>
    public required string Principal { get; init; }

    /// <summary>The identity provider id the principal was resolved from (part of the key: a renamed group is a new principal).</summary>
    public required string PrincipalExternalId { get; init; }

    public required string Privilege { get; init; }
    public UcLedgerState State { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? LastErrorCode { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The identity of the privilege; one ledger row per key and data source.</summary>
    public UcPrivilegeKey Key => new(SecurableType, FullName, PrincipalExternalId, Privilege);
}

/// <summary>The identity of one privilege on one securable for one principal (names normalized, ordinal comparison).</summary>
public readonly record struct UcPrivilegeKey(UcSecurableType SecurableType, string FullName, string PrincipalExternalId, string Privilege);

/// <summary>The provisioning state of one consent as reported to its consumers.</summary>
public enum UcGrantState
{
    Pending = 0,
    Provisioned = 1,
    Unsupported = 2,
    Failed = 3,
    Revoked = 4
}

/// <summary>The provisioning feedback of one consent; tenant scoped like the consent.</summary>
public sealed record UcGrantFeedback(
    TenantId Tenant,
    Guid ConsentId,
    string DataSource,
    UcGrantState State,
    string? ReasonCode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? VerifiedAt);

/// <summary>A feedback transition announced to consumers as a consent event: provisioned, or provisioning failed.</summary>
public sealed record UcFeedbackEvent(Guid ConsentId, TableIdentifier Table, bool Provisioned, DateTimeOffset OccurredAt);

/// <summary>Wire names of the provisioning enums as stored in the governance database.</summary>
public static class UcProvisioningStoreNames
{
    public static string ToStored(this UcLedgerState state) => state switch
    {
        UcLedgerState.Pending => "PENDING",
        UcLedgerState.Applied => "APPLIED",
        UcLedgerState.RevokePending => "REVOKE_PENDING",
        UcLedgerState.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown ledger state.")
    };

    /// <summary>An unknown stored state reads as <see cref="UcLedgerState.RevokePending"/>: the row is removed, never kept granting.</summary>
    public static UcLedgerState ParseLedgerState(string? value) => value switch
    {
        "PENDING" => UcLedgerState.Pending,
        "APPLIED" => UcLedgerState.Applied,
        "FAILED" => UcLedgerState.Failed,
        _ => UcLedgerState.RevokePending
    };

    public static string ToStored(this UcSecurableType type) => type switch
    {
        UcSecurableType.Catalog => "CATALOG",
        UcSecurableType.Schema => "SCHEMA",
        UcSecurableType.Table => "TABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown securable type.")
    };

    public static bool TryParseSecurableType(string? value, out UcSecurableType type)
    {
        switch (value)
        {
            case "CATALOG": type = UcSecurableType.Catalog; return true;
            case "SCHEMA": type = UcSecurableType.Schema; return true;
            case "TABLE": type = UcSecurableType.Table; return true;
            default: type = default; return false;
        }
    }

    public static string ToStored(this UcGrantState state) => state switch
    {
        UcGrantState.Pending => "PENDING",
        UcGrantState.Provisioned => "PROVISIONED",
        UcGrantState.Unsupported => "UNSUPPORTED",
        UcGrantState.Failed => "FAILED",
        UcGrantState.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown grant provisioning state.")
    };

    /// <summary>An unknown stored state reads as <see cref="UcGrantState.Pending"/>, never as provisioned.</summary>
    public static UcGrantState ParseGrantState(string? value) => value switch
    {
        "PROVISIONED" => UcGrantState.Provisioned,
        "UNSUPPORTED" => UcGrantState.Unsupported,
        "FAILED" => UcGrantState.Failed,
        "REVOKED" => UcGrantState.Revoked,
        _ => UcGrantState.Pending
    };
}

/// <summary>
/// Persistence of Unity Catalog provisioning: the desired-state read over consents, the ledger of applied privileges per data
/// source and the per-consent feedback. Ledger and feedback writes commit together with their audit entries (fail closed).
/// </summary>
public interface IUcProvisioningStore
{
    /// <summary>
    /// Every non-revoked table consent of <paramref name="tenant"/> on a table of <paramref name="dataSource"/> that has not ended before
    /// <paramref name="now"/> minus one day, hydrated with its column, row, row-scope and write rules. The pages are read in one
    /// transaction (a snapshot where the engine offers one); under concurrent changes without a snapshot a consent can be missed, so
    /// callers must not revoke on a single read while the set is changing. The caller applies the exact validity window.
    /// </summary>
    Task<IReadOnlyList<Consent>> ListTableConsentsAsync(TenantId tenant, string dataSource, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<UcLedgerEntry>> ListLedgerAsync(string dataSource, CancellationToken ct);

    /// <summary>The data sources that still hold ledger rows (including sources that are no longer provisioning-managed).</summary>
    Task<IReadOnlyList<string>> ListLedgerDataSourcesAsync(CancellationToken ct);

    /// <summary>
    /// Inserts or replaces ledger rows of <paramref name="dataSource"/> by their key, with the audit entries, in one transaction that
    /// first checks <paramref name="fencingToken"/> against the data source's fence (a lower token than the highest seen throws
    /// <see cref="Autheris.Application.State.FencingTokenRejectedException"/>).
    /// </summary>
    Task SaveLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct);

    /// <summary>Deletes ledger rows of <paramref name="dataSource"/> by their key, with the audit entries, fenced like <see cref="SaveLedgerAsync"/>.</summary>
    Task DeleteLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct);

    Task<IReadOnlyList<UcGrantFeedback>> ListFeedbackAsync(TenantId tenant, string dataSource, CancellationToken ct);

    /// <summary>The tenants that hold feedback rows on <paramref name="dataSource"/> that are not yet <see cref="UcGrantState.Revoked"/>.</summary>
    Task<IReadOnlyList<TenantId>> ListFeedbackTenantsAsync(string dataSource, CancellationToken ct);

    /// <summary>The feedback of the given consents of <paramref name="tenant"/>; consents without a row are absent.</summary>
    Task<IReadOnlyDictionary<Guid, UcGrantFeedback>> GetFeedbackAsync(TenantId tenant, IReadOnlyCollection<Guid> consentIds, CancellationToken ct);

    /// <summary>
    /// Inserts or replaces feedback rows of <paramref name="tenant"/> on <paramref name="dataSource"/>, with the audit entries, fenced like
    /// <see cref="SaveLedgerAsync"/>. <paramref name="events"/> are appended to the tenant's consent event outbox in the same transaction,
    /// as its last writes.
    /// </summary>
    Task SaveFeedbackAsync(
        TenantId tenant, string dataSource, long fencingToken, IReadOnlyList<UcGrantFeedback> rows, IReadOnlyList<AuditLogEntry> audit,
        IReadOnlyList<UcFeedbackEvent> events, CancellationToken ct);
}

/// <summary>
/// The store of a deployment without a PostgreSQL or SQL Server governance database: nothing is provisioned. Reads are empty;
/// writes fail, so a reconciler never believes it recorded a privilege.
/// </summary>
public sealed class UnavailableUcProvisioningStore : IUcProvisioningStore
{
    public static readonly UnavailableUcProvisioningStore Instance = new();

    private UnavailableUcProvisioningStore()
    {
    }

    public Task<IReadOnlyList<Consent>> ListTableConsentsAsync(TenantId tenant, string dataSource, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Consent>>([]);

    public Task<IReadOnlyList<UcLedgerEntry>> ListLedgerAsync(string dataSource, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UcLedgerEntry>>([]);

    public Task<IReadOnlyList<string>> ListLedgerDataSourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task SaveLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct) =>
        throw Unavailable();

    public Task DeleteLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct) =>
        throw Unavailable();

    public Task<IReadOnlyList<TenantId>> ListFeedbackTenantsAsync(string dataSource, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantId>>([]);

    public Task<IReadOnlyList<UcGrantFeedback>> ListFeedbackAsync(TenantId tenant, string dataSource, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UcGrantFeedback>>([]);

    public Task<IReadOnlyDictionary<Guid, UcGrantFeedback>> GetFeedbackAsync(TenantId tenant, IReadOnlyCollection<Guid> consentIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UcGrantFeedback>>(new Dictionary<Guid, UcGrantFeedback>());

    public Task SaveFeedbackAsync(
        TenantId tenant, string dataSource, long fencingToken, IReadOnlyList<UcGrantFeedback> rows, IReadOnlyList<AuditLogEntry> audit,
        IReadOnlyList<UcFeedbackEvent> events, CancellationToken ct) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Unity Catalog provisioning needs a PostgreSQL or SQL Server governance database.");
}
