namespace Autheris.Tests.Integration;

using Autheris.Application.Interfaces;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The seeded demo catalog lists SQL tables that have no database behind them. Governance workflow tests (consent, four eyes,
/// delegation, masking) that read such a table use deterministic synthetic rows instead. Registered first, the synthetic executor
/// wins the SQL executor lookup (<c>FirstOrDefault(e =&gt; e.SupportedType == DataSourceType.Sql)</c>). Tests of the governed SQL
/// execution itself use a real SQLite data source instead (see <see cref="ODataIntegrationTests"/>).
/// </summary>
internal static class SyntheticSqlData
{
    public static IWebHostBuilder UseSyntheticSqlData(this IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
            services.Insert(0, ServiceDescriptor.Singleton<IDataSourceExecutor, SyntheticSqlDataSourceExecutor>()));
}
