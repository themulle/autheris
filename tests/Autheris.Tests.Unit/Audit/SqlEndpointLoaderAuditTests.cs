using System.IO;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Audit;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

public sealed class SqlEndpointLoaderAuditTests
{
    [Fact]
    public void LoadedSqlEndpointDefinition_MustCarry_FullTableQueryAuditPolicy()
    {
        var registry = Substitute.For<ISqlEndpointRegistry>();
        var loader = new SqlEndpointLoader(registry);

        var tempDir = Path.Combine(Path.GetTempPath(), $"autheris-loader-test-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sqlFile = Path.Combine(tempDir, "get_customers.sql");
            File.WriteAllText(sqlFile, "-- @name: get_customers\n-- @summary: Retrieve customer list\nSELECT id, name FROM default.public.customers;");

            var def = loader.LoadFile(sqlFile);

            def.ShouldNotBeNull();
            def.AuditPolicy.ShouldNotBeNull();
            def.AuditPolicy.Level.ShouldBe(AuditLevel.Full);
            def.AuditPolicy.EventType.ShouldBe(AuditEventTypes.TableQuery);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
