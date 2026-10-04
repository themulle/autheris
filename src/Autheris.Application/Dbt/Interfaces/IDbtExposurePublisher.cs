namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IDbtExposurePublisher
{
    Task<string> GenerateExposuresYamlAsync(CancellationToken ct = default);
    Task ExportExposuresFileAsync(string outputFilePath, CancellationToken ct = default);
}
