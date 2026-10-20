namespace Autheris.Domain.Governance;

using System;

public enum RecertClass
{
    Privileged = 1,
    Standard = 2
}

public enum RecertCampaignStatus
{
    Active = 1,
    Completed = 2,
    Expired = 3
}

public enum RecertDecision
{
    Pending = 1,
    Certified = 2,
    Revoked = 3
}

public sealed record RecertificationCampaign(
    string TenantId,
    string CampaignId,
    string Name,
    RecertClass RecertClass,
    RecertCampaignStatus Status,
    DateTimeOffset Deadline,
    DateTimeOffset CreatedAt
);

public sealed record RecertificationItem(
    string TenantId,
    string CampaignId,
    string ItemId,
    string AssetType,
    string AssetKey,
    string AssignedTo,
    string ReviewerId,
    RecertDecision Decision = RecertDecision.Pending,
    DateTimeOffset? DecisionAt = null,
    string? DecisionBy = null,
    string? Comments = null
);
