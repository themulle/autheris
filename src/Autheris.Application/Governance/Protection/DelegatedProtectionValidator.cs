using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Protection;

/// <summary>
/// Enforces fail-closed protection boundaries on data sources whose <see cref="EnforcementMode"/> is
/// <see cref="EnforcementMode.Delegated"/> without defence-in-depth.
/// </summary>
public static class DelegatedProtectionValidator
{
    public const string ErrorCode = "DELEGATED_PROTECTION_VIOLATION";

    public static void Validate(
        EnforcementMode mode,
        string sourceName,
        EffectiveProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var hasMaskCategory = !string.IsNullOrWhiteSpace(protection.TableMaskCategory) ||
                              protection.Columns.Values.Any(c => !string.IsNullOrWhiteSpace(c.EffectiveMaskCategory));

        Validate(mode, sourceName, protection.TableClass, hasMaskCategory ? "MASK_CATEGORY" : null);
    }

    public static void Validate(
        EnforcementMode mode,
        string sourceName,
        ProtectionClass effectiveClass,
        string? maskCategory)
    {
        if (mode != EnforcementMode.Delegated)
        {
            return;
        }

        if (effectiveClass >= ProtectionClass.Confidential)
        {
            throw new ValidationException(
                $"Security violation ({ErrorCode}): Data source '{sourceName}' uses EnforcementMode = Delegated without defence-in-depth and cannot hold objects with protection class '{effectiveClass.ToCanonicalName()}'.");
        }

        if (!string.IsNullOrWhiteSpace(maskCategory))
        {
            throw new ValidationException(
                $"Security violation ({ErrorCode}): Data source '{sourceName}' uses EnforcementMode = Delegated without defence-in-depth and cannot hold objects with mask category '{maskCategory}'.");
        }
    }

    public static void Validate(
        GatewayOptions options,
        IEnumerable<(string DataSource, EffectiveProtection Protection)>? protections = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (protections is null)
        {
            foreach (var (name, connection) in options.DataSources.Connections)
            {
                if (connection is { EnforcementMode: EnforcementMode.Delegated })
                {
                    Validate(connection.EnforcementMode, name, ProtectionClass.Confidential, maskCategory: null);
                }
            }

            return;
        }

        foreach (var (sourceName, protection) in protections)
        {
            if (protection is null)
            {
                continue;
            }

            var mode = SqlDataSourceResolver.ResolveConnectionOptions(options, sourceName)?.EnforcementMode
                ?? EnforcementMode.AutherisEnforced;
            Validate(mode, sourceName, protection);
        }
    }
}
