namespace Autheris.Application.Governance.Services;

/// <summary>
/// The caller may not apply a declarative consent manifest (#667). The message is fixed; the endpoint answers 403 <c>ACCESS_DENIED</c>.
/// </summary>
public sealed class ConsentApplyForbiddenException()
    : Exception("The caller lacks the role required to apply declarative consent manifests.");
