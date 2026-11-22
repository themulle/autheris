using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Policy;

/// <summary>Validates deployment-level <see cref="AccessPolicyOptions"/> at startup and configuration binding.</summary>
public sealed class AccessPolicyOptionsValidator : IValidateOptions<AccessPolicyOptions>
{
    public const int MaxReminderEntries = 10;
    public const int MaxReminderDay = 365;
    public const int MaxTrustedIssuerEntries = 100;
    public const int MaxEmployeeGroupEntries = 200;
    public const int MaxServicePrincipalClientEntries = 200;

    public ValidateOptionsResult Validate(string? name, AccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = ValidateCore(options);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    public static IReadOnlyList<string> ValidateCore(AccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        ValidatePositiveBound(options.MaxDataOwnerDays, 36500, nameof(AccessPolicyOptions.MaxDataOwnerDays), failures);
        ValidatePositiveBound(options.MaxGovernanceDays, 36500, nameof(AccessPolicyOptions.MaxGovernanceDays), failures);
        ValidatePositiveBound(options.MaxItAdminDays, 3650, nameof(AccessPolicyOptions.MaxItAdminDays), failures);
        ValidatePositiveBound(options.MaxSelfGrantDays, 3650, nameof(AccessPolicyOptions.MaxSelfGrantDays), failures);
        ValidatePositiveBound(options.MaxBundleItems, 1000, nameof(AccessPolicyOptions.MaxBundleItems), failures);
        ValidatePositiveBound(options.MaxPendingRequestsPerRequester, 10000, nameof(AccessPolicyOptions.MaxPendingRequestsPerRequester), failures);
        ValidatePositiveBound(options.MaxTablesPerAssignment, 10000, nameof(AccessPolicyOptions.MaxTablesPerAssignment), failures);
        ValidatePositiveBound(options.MaxPageSize, 5000, nameof(AccessPolicyOptions.MaxPageSize), failures);

        if (!Enum.IsDefined(options.SelfGrantCeiling) || options.SelfGrantCeiling == ProtectionClass.Secret)
        {
            failures.Add("AccessPolicy:SelfGrantCeiling cannot be Secret and must be Public, Internal, or Confidential.");
        }

        if (!Enum.IsDefined(options.DefaultContainerVisibility))
        {
            failures.Add("AccessPolicy:DefaultContainerVisibility must be Open or OnRequest.");
        }

        ValidateReminderDays(options.ReminderDays, failures);
        ValidateInternalSettings(options.Internal, failures);

        return failures;
    }

    internal static void ValidateReminderDays(IReadOnlyList<int>? reminderDays, List<string> failures)
    {
        if (reminderDays is null)
        {
            failures.Add("AccessPolicy:ReminderDays must not be null.");
            return;
        }

        if (reminderDays.Count > MaxReminderEntries)
        {
            failures.Add($"AccessPolicy:ReminderDays must contain at most {MaxReminderEntries} entries.");
        }

        foreach (var day in reminderDays)
        {
            if (day < 1 || day > MaxReminderDay)
            {
                failures.Add($"AccessPolicy:ReminderDays entries must be between 1 and {MaxReminderDay} (got {day}).");
            }
        }
    }

    internal static void ValidateInternalSettings(InternalAccessPolicySettings? settings, List<string> failures)
    {
        if (settings is null)
        {
            failures.Add("AccessPolicy:Internal must not be null.");
            return;
        }

        ValidateIssuerList(settings.TrustedIssuerTenantIds, failures);
        ValidateEmployeeGroupSids(settings.EmployeeGroupSids, failures);
        ValidateServicePrincipalClients(settings.TrustedServicePrincipalClientIds, failures);
    }

    internal static void ValidateIssuerList(IReadOnlyList<string>? issuers, List<string> failures)
    {
        if (issuers is null)
        {
            return;
        }

        if (issuers.Count > MaxTrustedIssuerEntries)
        {
            failures.Add($"AccessPolicy:Internal:TrustedIssuerTenantIds must contain at most {MaxTrustedIssuerEntries} entries.");
        }

        foreach (var raw in issuers)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw.Trim(), out var parsed) || parsed == Guid.Empty)
            {
                failures.Add($"AccessPolicy:Internal:TrustedIssuerTenantIds contains an invalid tenant GUID.");
            }
        }
    }

    internal static void ValidateEmployeeGroupSids(IReadOnlyList<string>? sids, List<string> failures)
    {
        if (sids is null)
        {
            return;
        }

        if (sids.Count > MaxEmployeeGroupEntries)
        {
            failures.Add($"AccessPolicy:Internal:EmployeeGroupSids must contain at most {MaxEmployeeGroupEntries} entries.");
        }

        foreach (var raw in sids)
        {
            if (!SidFormat.IsCanonical(raw?.Trim()))
            {
                failures.Add("AccessPolicy:Internal:EmployeeGroupSids contains a non-canonical SID.");
            }
        }
    }

    internal static void ValidateServicePrincipalClients(IReadOnlyList<string>? clients, List<string> failures)
    {
        if (clients is null)
        {
            return;
        }

        if (clients.Count > MaxServicePrincipalClientEntries)
        {
            failures.Add($"AccessPolicy:Internal:TrustedServicePrincipalClientIds must contain at most {MaxServicePrincipalClientEntries} entries.");
        }

        foreach (var raw in clients)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 128 || trimmed.Any(char.IsControl))
            {
                failures.Add("AccessPolicy:Internal:TrustedServicePrincipalClientIds contains an invalid client identifier.");
            }
        }
    }

    private static void ValidatePositiveBound(int value, int upperBound, string name, List<string> failures)
    {
        if (value < 1 || value > upperBound)
        {
            failures.Add($"AccessPolicy:{name} must be between 1 and {upperBound} (got {value}).");
        }
    }
}
