namespace Autheris.Application.Catalog.Semantic;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Core port implemented by the dbt module. The core webhook route calls it only after the HMAC signature of the
/// payload has been verified, so implementations never verify signatures themselves.
/// </summary>
public interface IDbtCloudWebhookHandler
{
    Task<DbtWebhookProcessingResult> HandleVerifiedAsync(string payload, CancellationToken ct);
}
