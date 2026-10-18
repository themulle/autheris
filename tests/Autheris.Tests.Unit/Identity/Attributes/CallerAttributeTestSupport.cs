namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

internal static class CallerAttributeTestSupport
{
    internal const string TrustedIssuer = "https://login.example/tenant/v2.0";
    internal const string Bearer = "AuthenticationTypes.Federation";
    internal static readonly TenantId Tenant = new("tenant_a");
    internal static readonly string UserOid = "11111111-1111-1111-1111-111111111111";
    internal static readonly string OtherOid = "22222222-2222-2222-2222-222222222222";

    internal static CallerAttributeOptions Options(bool enabled = true, params string[] personIssuers) => new()
    {
        Enabled = enabled,
        Claims = new CallerAttributeClaimsOptions
        {
            TrustedIssuers = [new CallerAttributeTrustedIssuer { Issuer = TrustedIssuer, SubjectKeyIsPerson = personIssuers.Contains(TrustedIssuer) }]
        }
    };

    /// <summary>A principal with one identity of <paramref name="authenticationType"/>; claim issuer defaults to the trusted issuer.</summary>
    internal static ClaimsPrincipal Principal(string? authenticationType, IEnumerable<(string Type, string Value)> claims, string issuer = TrustedIssuer)
    {
        var identity = new ClaimsIdentity(authenticationType);
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value, ClaimValueTypes.String, issuer));
        }

        return new ClaimsPrincipal(identity);
    }

    internal static ClaimsPrincipal EntraUser(string oid = "", params (string Type, string Value)[] extra) =>
        Principal(Bearer, new[] { ("oid", string.IsNullOrEmpty(oid) ? UserOid : oid), ("upn", "alice@example.com"), ("tid", "tenant_a") }.Concat(extra));

    internal static CallerSubject UserSubject(IEnumerable<(string Type, string Value)>? claims = null, string? authenticationType = Bearer, string issuer = TrustedIssuer)
    {
        var principal = Principal(authenticationType, new[] { ("oid", UserOid) }.Concat(claims ?? []), issuer);
        return CallerSubjectFactory.Create(principal, Tenant, Options());
    }

    internal static CallerAttributeDefinition Definition(
        string name = "cost_center",
        string providerKind = "claims",
        CallerAttributeType type = CallerAttributeType.Text,
        CallerAttributeCardinality cardinality = CallerAttributeCardinality.List,
        int maxValues = 50,
        int ttlSeconds = 60,
        params (string Key, string Value)[] config) => new(
        Guid.NewGuid(), Tenant, name, type, cardinality, maxValues, ttlSeconds,
        new CallerAttributeProviderBinding(providerKind, config.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Last().Value)), null, FilterApprovalStatus.Active);

    internal static CallerAttributeDefinition ClaimsDefinition(string name = "cost_center", string claim = "department", CallerAttributeCardinality cardinality = CallerAttributeCardinality.List, CallerAttributeType type = CallerAttributeType.Text, params (string Key, string Value)[] extra) =>
        Definition(name, "claims", type, cardinality, 50, 60, [("claim", claim), ("issuer", TrustedIssuer), ("idpControlledAck", "true"), .. extra]);
}
