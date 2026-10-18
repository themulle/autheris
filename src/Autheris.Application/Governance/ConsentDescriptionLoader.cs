namespace Autheris.Application.Governance;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// PLAN-GOV-158-INC1: the explicit opt-in load of <c>CONSENTS.description</c>. The read methods of <see cref="IConsentRepository"/> never load it (the access
/// decision hot path does not need it); explain, consent listings, myActiveConsents and the manifest compare call this once for the consents they present.
/// A repository without description support leaves the consents as they are (descriptions stay unset, never an error).
/// </summary>
public static class ConsentDescriptionLoader
{
    public static Task LoadAsync(IConsentRepository repository, IReadOnlyCollection<Consent> consents, CancellationToken ct) =>
        consents.Count == 0 || repository is not IConsentDescriptionRepository descriptions
            ? Task.CompletedTask
            : descriptions.LoadDescriptionsAsync(consents, ct);
}
