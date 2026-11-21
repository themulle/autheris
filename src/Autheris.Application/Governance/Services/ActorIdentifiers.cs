namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

internal static class ActorIdentifiers
{
    internal static HashSet<string> Collect(ClaimsPrincipal? caller, Sid actorSid, SecurityPrincipalContext? callerContext) =>
        SeparationOfDutiesService.CollectActorIdentifiers(caller, actorSid, callerContext);

    internal static bool IsActorSubject(HashSet<string> actorIdentifiers, string? granteeRef) =>
        SeparationOfDutiesService.AnyOverlap(actorIdentifiers, granteeRef);

    internal static void AddIdentifierForms(HashSet<string> set, string? value) =>
        SeparationOfDutiesService.AddIdentifierForms(set, value);
}
