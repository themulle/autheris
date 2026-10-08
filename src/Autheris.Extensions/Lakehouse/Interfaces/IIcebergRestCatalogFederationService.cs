namespace Autheris.Extensions.Lakehouse.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-DATA-05: Apache Iceberg REST Catalog (IRC) Federation and Dynamic STS Credential Vending service.
/// </summary>
public interface IIcebergRestCatalogFederationService
{
    ValueTask<IReadOnlyList<string>> ListNamespacesAsync(string tenantId, ClaimsPrincipal principal, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> ListTablesAsync(string tenantId, string @namespace, ClaimsPrincipal principal, CancellationToken ct = default);
    ValueTask<IcebergLoadTableResponse> LoadTableAsync(string tenantId, string @namespace, string table, ClaimsPrincipal principal, CancellationToken ct = default);
    ValueTask<VendedStorageCredential> VendCredentialAsync(string tenantId, string @namespace, string table, ClaimsPrincipal principal, CancellationToken ct = default);
}
