namespace Autheris.Domain.Model;

using System;

/// <summary>Per-grant classification access entry persisted in <c>CONSENT_CLASSIFICATION_ACCESS</c>.</summary>
public sealed record ConsentClassificationAccess
{
    public required string TenantId { get; init; }
    public required Guid ConsentId { get; init; }
    public required string Code { get; init; }
    public required ColumnAccessLevel AccessLevel { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public string? ApprovedBySid { get; init; }
    public DateTimeOffset ApprovedAt { get; init; }
}
