namespace Autheris.Application.Interfaces;

using System.Security.Claims;
using Autheris.Domain.Model;

public interface IIdentitySubjectResolver
{
    SubjectIdentity ResolveSubject(ClaimsPrincipal? principal);
}
