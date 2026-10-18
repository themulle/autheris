namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Issue #34: Self-service consent request DTO for own requests and approver inboxes.
/// </summary>
public sealed class ConsentRequestDto
{
    public Guid Id { get; init; }
    public Guid TableId { get; init; }
    public string TableIdentifier { get; init; } = string.Empty;
    public string RequesterSid { get; init; } = string.Empty;
    public string RequestedGranteeType { get; init; } = string.Empty;
    public string? RequestedGranteeRef { get; init; }
    public string? BusinessJustification { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? RequestedValidTo { get; init; }
    public string? ItsmTicketId { get; init; }
}

/// <summary>
/// Issue #34: Active consent summary DTO.
/// </summary>
public sealed class ConsentDto
{
    public Guid Id { get; init; }
    public string TableIdentifier { get; init; } = string.Empty;
    public string Effect { get; init; } = string.Empty;
    public string GranteeType { get; init; } = string.Empty;
    public string? GranteeSid { get; init; }
    public string? RoleName { get; init; }
    public DateTimeOffset ValidFrom { get; init; }
    public DateTimeOffset ValidTo { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>PLAN-GOV-158-INC1: owner-authored description (output only through <c>ConsentDescriptionText.ForOutput</c>); <c>null</c> when none or not trusted.</summary>
    public string? Description { get; init; }
}
