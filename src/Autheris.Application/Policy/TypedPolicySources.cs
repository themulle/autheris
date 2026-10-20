namespace Autheris.Application.Policy;

using System;

/// <summary>
/// The typed policy producers a decision site hands to <see cref="TableAccessPolicy"/> (WP-X0l): the row policy factory (consent, access
/// profile, Virtual Filter, Casbin merge) and the column mask factory. A site without it produces string-only decisions, which the compiler
/// path denies; DI registers it once for every site.
/// </summary>
public sealed record TypedPolicySources(
    RowPolicyFactory RowPolicies,
    ColumnMaskPolicyFactory MaskPolicies,
    Autheris.Application.Identity.Attributes.ICallerAttributeResolver? CallerAttributes = null,
    Autheris.Application.Plugins.Auth.IAuthPluginHost? AuthPlugins = null);
