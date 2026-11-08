namespace Autheris.Application.Usage;

public enum UsageOutcome : byte
{
    Allow,
    DenyAccess,
    DenyRateLimit,
    DenyCost,
    DenyQuota,
    DenyOther,
    Error
}
