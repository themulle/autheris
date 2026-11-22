using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class RealmBoundSidArchitectureTests
{
    private static readonly Assembly[] Production =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly
    ];

    private static readonly HashSet<string> AllowedRawOidFiles = new(StringComparer.Ordinal)
    {
        "src/Autheris.Api/Endpoints/GovernanceEndpoints.Access.cs",
        "src/Autheris.Api/Endpoints/McpEndpoints.cs",
        "src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs",
        "src/Autheris.Api/Middleware/IncrementalDeliveryMiddleware.cs",
        "src/Autheris.Api/Middleware/ResourceGroupMiddleware.cs",
        "src/Autheris.Api/Middleware/SubjectLockoutMiddleware.cs",
        "src/Autheris.Api/Security/ClaimsNormalizer.cs",
        "src/Autheris.Application/Catalog/External/AppOnlyPrincipal.cs",
        "src/Autheris.Application/Common/Interfaces/ITokenRevocationService.cs",
        "src/Autheris.Application/Governance/ConsentRequests/ConsentDecisionGuard.cs",
        "src/Autheris.Application/Governance/ConsentRequests/SeparationOfDutiesService.cs",
        "src/Autheris.Application/Identity/Attributes/CallerSubjectFactory.cs",
        "src/Autheris.Application/Identity/RealmUserKeyResolver.cs",
        "src/Autheris.Application/Identity/ScimIdentityCorrelator.cs",
        "src/Autheris.Application/Services/IdentitySubjectResolver.cs",
        "src/Autheris.Domain/Common/Sid.cs",
        "src/Autheris.Domain/Identity/IdentityRealm.cs",
        "src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs"
    };

    private static readonly Regex RawOidLiteralPattern = new(
        "\"oid\"|\"http://schemas\\.microsoft\\.com/identity/claims/objectidentifier\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RawOidOrSubPattern = new(
        "\"oid\"|\"sub\"|\"http://schemas\\.microsoft\\.com/identity/claims/objectidentifier\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "Security")]
    public void No_raw_oid_as_subject_key()
    {
        var unvalidatedEntraSidCalls = new List<string>();
        int? firstPipelineCheckOffset = null;
        int? firstRawOidOffset = null;

        foreach (var assembly in Production)
        {
            var module = CecilModuleCache.For(assembly.Location);
            foreach (var type in module.GetTypes())
            {
                var isSidFormatItself = type.FullName == "Autheris.Domain.Common.SidFormat";
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (!isSidFormatItself &&
                            ins.Operand is MethodReference m &&
                            m.DeclaringType.FullName == "Autheris.Domain.Common.SidFormat" &&
                            m.Name == "TryFromEntraObjectId" &&
                            m.Parameters.Count == 2)
                        {
                            unvalidatedEntraSidCalls.Add($"{type.FullName}.{method.Name}");
                        }
                    }

                    if (type.FullName == "Autheris.Application.Services.IdentitySubjectResolver" &&
                        method.Name == "ResolveSubject")
                    {
                        foreach (var ins in method.Body.Instructions)
                        {
                            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s)
                            {
                                if (firstPipelineCheckOffset is null &&
                                    (s == "Autheris:IdentityPipeline" ||
                                     s == "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid"))
                                {
                                    firstPipelineCheckOffset = ins.Offset;
                                }

                                if (firstRawOidOffset is null &&
                                    (s == "oid" || s == "http://schemas.microsoft.com/identity/claims/objectidentifier"))
                                {
                                    firstRawOidOffset = ins.Offset;
                                }
                            }
                            else if (firstPipelineCheckOffset is null &&
                                     ins.Operand is MethodReference mr &&
                                     mr.Name.Contains("PipelinePrimarySid", StringComparison.Ordinal))
                            {
                                firstPipelineCheckOffset = ins.Offset;
                            }
                        }
                    }
                }
            }
        }

        unvalidatedEntraSidCalls.ShouldBeEmpty(
            "Production code must use the realm/issuer-validated SidFormat.TryFromEntraObjectId overload: " +
            string.Join(", ", unvalidatedEntraSidCalls));

        firstPipelineCheckOffset.ShouldNotBeNull("IdentitySubjectResolver.ResolveSubject must check pipeline PrimarySid.");
        firstRawOidOffset.ShouldNotBeNull("Positive control: IdentitySubjectResolver.ResolveSubject fallback still handles oid.");
        firstPipelineCheckOffset.Value.ShouldBeLessThan(
            firstRawOidOffset.Value,
            "IdentitySubjectResolver.ResolveSubject must prefer pipeline-minted PrimarySid over raw oid/sub claims.");

        var unexpectedRawOidFiles = new List<string>();
        var forbiddenStorageOrPolicyRefs = new List<string>();

        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (file.RelativePath.StartsWith("src/Autheris.Infrastructure/", StringComparison.Ordinal) ||
                file.RelativePath.StartsWith("src/Autheris.Application/Policy/", StringComparison.Ordinal) ||
                file.RelativePath.StartsWith("src/Autheris.Application/Governance/Grants/", StringComparison.Ordinal) ||
                file.RelativePath.StartsWith("src/Autheris.Application/Governance/Services/", StringComparison.Ordinal) ||
                file.RelativePath.StartsWith("src/Autheris.Application/ConsentRequests/", StringComparison.Ordinal))
            {
                if (RawOidOrSubPattern.IsMatch(file.Text))
                {
                    forbiddenStorageOrPolicyRefs.Add(file.RelativePath);
                }
            }

            if (RawOidLiteralPattern.IsMatch(file.Text) && !AllowedRawOidFiles.Contains(file.RelativePath))
            {
                unexpectedRawOidFiles.Add(file.RelativePath);
            }
        }

        forbiddenStorageOrPolicyRefs.ShouldBeEmpty(
            "Storage, policy, grant, and consent pipelines must never read raw oid/sub directly: " +
            string.Join(", ", forbiddenStorageOrPolicyRefs));
        unexpectedRawOidFiles.ShouldBeEmpty(
            "Unexpected raw oid literal outside the vetted claim-normalization allowlist: " +
            string.Join(", ", unexpectedRawOidFiles));
    }
}
