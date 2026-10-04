namespace Autheris.Application.Integrations.Backstage;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IBackstageCatalogExportService
{
    Task<IReadOnlyList<BackstageEntity>> ExportCatalogEntitiesAsync(
        string? kindFilter = null,
        string? typeFilter = null,
        CancellationToken cancellationToken = default);

    Task<BackstageEntity?> ExportEntityByNameAsync(
        string entityName,
        CancellationToken cancellationToken = default);

    Task<string> ExportCatalogEntitiesYamlAsync(
        string? kindFilter = null,
        string? typeFilter = null,
        CancellationToken cancellationToken = default);
}
