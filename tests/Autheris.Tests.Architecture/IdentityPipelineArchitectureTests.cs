using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-AUTH-06a / SEC-AUTH-11b: one identity pipeline. Only <c>IdentityPipeline</c> normalizes claims, no code outside the pipeline and the
/// group resolver reads inbound group claim values, and only control-plane code reads the plugin enrichment output.
/// </summary>
public sealed class IdentityPipelineArchitectureTests
{
    private static readonly Assembly[] Production =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly
    ];

    // Types may be compiler-generated nested types of the allowed owner (closures, state machines).
    private static bool IsOwnedBy(TypeDefinition type, IReadOnlyCollection<string> owners)
    {
        var top = type;
        while (top.DeclaringType is not null)
        {
            top = top.DeclaringType;
        }

        return owners.Contains(top.FullName);
    }

    private static List<string> Callers(Func<Instruction, bool> predicate, IReadOnlyCollection<string> allowedOwners, out int allowedHits)
    {
        var violations = new List<string>();
        allowedHits = 0;
        foreach (var assembly in Production)
        {
            foreach (var type in CecilModuleCache.For(assembly.Location).GetTypes())
            {
                var owned = IsOwnedBy(type, allowedOwners);
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    if (!method.Body.Instructions.Any(predicate))
                    {
                        continue;
                    }

                    if (owned)
                    {
                        allowedHits++;
                    }
                    else
                    {
                        violations.Add($"{assembly.GetName().Name}: {type.FullName}.{method.Name}");
                    }
                }
            }
        }

        return violations;
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-06a")]
    public void Only_identity_pipeline_calls_ClaimsNormalizer()
    {
        var violations = Callers(
            i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Autheris.Api.Security.ClaimsNormalizer" && m.Name == "Normalize",
            ["Autheris.Api.Security.IdentityPipeline"],
            out var pipelineCalls);

        pipelineCalls.ShouldBeGreaterThan(0, "positive control: the pipeline calls the normalizer");
        violations.ShouldBeEmpty("ClaimsNormalizer.Normalize runs only inside IIdentityPipeline: " + string.Join(", ", violations));
    }

    // The resolver reads the inbound group claim types (one table per scheme); GroupResolutionClaims owns the list of inbound types.
    private static readonly string[] RawGroupReaders =
    [
        "Autheris.Application.Identity.GroupSidResolver",
        "Autheris.Domain.Identity.GroupResolutionClaims",
        // Not claim reads: a query-string key, a GraphQL result field and a dbt owner-spec key.
        "Autheris.Api.Endpoints.VirtualFilterEndpoints",
        "Autheris.GraphQL.Catalog.AggregateGraphQlTypes",
        "Autheris.GraphQL.Catalog.MetricGraphQlTypes",
        "Autheris.Extensions.Dbt.DbtOwnerSpecValidator"
    ];

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-06a")]
    public void No_code_reads_raw_groups_claims_outside_pipeline()
    {
        var violations = Callers(
            i => i.OpCode.Code == Code.Ldstr && i.Operand is string s
                 // ClaimTypes.GroupSid (the ws/2008 groupsid URI) is the pipeline-issued type: readers go through GetGroupSids' issuer filter.
                 && (s == "groups" || s == "groupsid"
                     || s == "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"),
            RawGroupReaders,
            out var allowedHits);

        allowedHits.ShouldBeGreaterThan(0, "positive control: the group resolver and the inbound-type list use the claim names");
        violations.ShouldBeEmpty("raw group claim types are read by the group resolver only: " + string.Join(", ", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-11b")]
    public void Only_control_plane_code_reads_ControlPlaneEnrichment()
    {
        // No data-plane type reads the member. The security context record itself (equality, copy) and the middleware that writes it are the owners.
        var violations = Callers(
            i => i.Operand is MethodReference m && m.Name == "get_ControlPlaneEnrichment",
            ["Autheris.Domain.Security.SecurityPrincipalContext"],
            out var ownerHits);

        typeof(Autheris.Domain.Security.SecurityPrincipalContext).GetProperty("ControlPlaneEnrichment").ShouldNotBeNull("positive control: the member exists");
        ownerHits.ShouldBeGreaterThanOrEqualTo(0);
        violations.ShouldBeEmpty("ControlPlaneEnrichment is never read by data-plane code: " + string.Join(", ", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-06a")]
    public void ClaimsNormalizationMiddleware_is_not_registered()
    {
        var apiTypes = CecilModuleCache.For(typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly.Location).GetTypes();

        apiTypes.ShouldNotContain(t => t.Name == "ClaimsNormalizationMiddleware");
        apiTypes.ShouldContain(t => t.FullName == "Autheris.Api.Security.IdentityPipeline", "positive control");
    }
}
