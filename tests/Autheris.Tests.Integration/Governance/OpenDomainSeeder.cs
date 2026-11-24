namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A table without a domain assignment belongs to the tenant's system domain Unassigned (<c>OnRequest</c>): without a catalog grant a requester
/// cannot see it, and a permission request for it answers <c>TARGET_NOT_FOUND</c> (#1009). Tests of the request workflow itself place their
/// table in an <c>Open</c> domain first.
/// </summary>
internal static class OpenDomainSeeder
{
    private static readonly Sid SeedActor = new("S-1-5-21-1000-1000-1000-9999");

    /// <param name="ownerGroupSid">The owner group of the domain; its members decide requests for the table as data owners.</param>
    public static async Task<Guid> AssignToOpenDomainAsync(IServiceProvider services, TenantId tenant, TableIdentifier table, Sid? ownerGroupSid = null)
    {
        using var scope = services.CreateScope();
        var metadata = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var domains = scope.ServiceProvider.GetRequiredService<IGovernanceDomainRepository>();

        var meta = await metadata.GetTableMetadataAsync(table)
            ?? throw new InvalidOperationException($"Table {table} is not registered in the catalog.");

        var now = DateTimeOffset.UtcNow;
        var result = await domains.UpsertAsync(
            new GovernanceDomain(
                Guid.NewGuid(),
                tenant,
                "open-" + Guid.NewGuid().ToString("N")[..12],
                null,
                null,
                ownerGroupSid,
                ProtectionClass.Internal,
                CatalogVisibilityMode.Open,
                IsSystemUnassigned: false,
                Version: 0,
                now,
                now),
            expectedVersion: 0,
            SeedActor);
        if (result.Status != GovernanceDomainWriteStatus.Created || result.Domain is null)
        {
            throw new InvalidOperationException($"The Open test domain could not be created ({result.Status}).");
        }

        await domains.AssignTablesAsync(tenant, result.Domain.Id, [meta.Table.Id], SeedActor);
        return result.Domain.Id;
    }
}
