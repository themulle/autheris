namespace Autheris.Application.Sql.ListParameters;

using System;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>Limits of list-valued client parameters (REQ-SQL-128). The internal constants are not options (Phase 3 14.7).</summary>
public readonly record struct ListParameterLimits(int ConfiguredMaxValues, int MaxListParameters)
{
    /// <summary>Documented range of <c>WebSql:MaxListParameterValues</c>.</summary>
    public const int MaxConfigurable = 10_000;

    /// <summary>Documented range of <c>WebSql:MaxListParameters</c>.</summary>
    public const int MaxListParametersCeiling = 64;

    /// <summary>Hard ceiling of the raw array length, whatever the configuration (SEC-128-09).</summary>
    public const int RawListCeiling = 100_000;

    /// <summary>Headroom below the dialect bind budget (owner decision Q-128-1): the budget also holds tenant, policy and literal binds.</summary>
    public const int BindBudgetHeadroom = 100;

    /// <summary>Hard ceiling of the raw array length: <c>min(10 x configured, 100,000)</c> (SEC-128-09), checked before any element is read.</summary>
    public int RawCeiling => (int)Math.Min(10L * ConfiguredMaxValues, RawListCeiling);

    public static ListParameterLimits From(WebSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ListParameterLimits(
            Math.Clamp(options.MaxListParameterValues, 1, MaxConfigurable),
            Math.Clamp(options.MaxListParameters, 1, MaxListParametersCeiling));
    }

    /// <summary><c>min(configured, MaxBindParameters - 100, MaxInListItems ?? int.MaxValue)</c> (A-6).</summary>
    public static int EffectiveMax(int configured, DialectCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        int cap = Math.Min(capabilities.MaxBindParameters - BindBudgetHeadroom, capabilities.MaxInListItems ?? int.MaxValue);
        return Math.Max(0, Math.Min(configured, cap));
    }
}
