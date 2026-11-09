using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-AUTH-03c: a group SID claim that is not stamped with the pipeline issuer is invisible to <c>GetGroupSids</c> and would silently
/// empty a principal's group set (dropping group-scoped denies and row filters). Every writer mints it through
/// <c>IdentityClaimIssuers.CreateGroupSidClaim</c>; constructing a two-argument <see cref="System.Security.Claims.Claim"/> next to the GroupSid claim type fails the build.
/// </summary>
public sealed class IdentityGroupClaimWriterTests
{
    private const string GroupSidClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid";

    private static readonly Assembly[] Production =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly
    ];

    private static (List<string> Mentions, List<string> Violations) Scan()
    {
        var mentions = new List<string>();
        var violations = new List<string>();
        foreach (var assembly in Production)
        {
            foreach (var type in CecilModuleCache.For(assembly.Location).GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    var instructions = method.Body.Instructions;
                    if (!instructions.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == GroupSidClaimType))
                    {
                        continue;
                    }

                    var name = $"{assembly.GetName().Name}: {type.FullName}.{method.Name}";
                    mentions.Add(name);
                    var unstamped = instructions.Any(i => i.OpCode.Code == Code.Newobj
                        && i.Operand is MethodReference ctor
                        && ctor.DeclaringType.FullName == "System.Security.Claims.Claim"
                        && ctor.Parameters.Count == 2
                        && ctor.Parameters.All(p => p.ParameterType.FullName == "System.String"));
                    if (unstamped)
                    {
                        violations.Add(name);
                    }
                }
            }
        }

        return (mentions, violations);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AUTH-03c")]
    public void Every_group_claim_writer_stamps_pipeline_issuer()
    {
        var (mentions, violations) = Scan();

        mentions.ShouldNotBeEmpty("positive control: the scanner sees the issuer constants and readers of the GroupSid claim type");
        violations.ShouldBeEmpty("a GroupSid claim must be created with IdentityClaimIssuers.CreateGroupSidClaim: " + string.Join(", ", violations));
    }
}
