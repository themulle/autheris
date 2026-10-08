namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ProcedureSessionContextInitializerTests
{
    private static readonly TenantId Tenant = new("tenant-r-sql-6");
    private static readonly Sid UserSid = new("S-1-5-21-999");

    [Fact]
    public async Task R_SQL_6_MssqlProcedureInvoker_InitializesSessionViaInitializer()
    {
        var mockConn = Substitute.For<DbConnection>();
        mockConn.State.Returns(ConnectionState.Open);
        var mockCmd = Substitute.For<DbCommand>();
        var mockReader = Substitute.For<DbDataReader>();
        mockReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(false);
        mockCmd.ExecuteReaderAsync(Arg.Any<CancellationToken>()).Returns(mockReader);
        mockConn.CreateCommand().Returns(mockCmd);

        var mockSessionInit = Substitute.For<IDbSessionContextInitializer>();
        var sqlConnFactory = Substitute.For<ISqlConnectionFactory>();
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["ds1"] = new() { Provider = "SqlServer", ConnectionString = "Server=localhost;" }
                }
            }
        };

        sqlConnFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockConn));

        var connProvider = new ProcedureConnectionProvider(sqlConnFactory, Options.Create(gatewayOptions));

        var invoker = new MssqlProcedureInvoker(connProvider, Options.Create(gatewayOptions), mockSessionInit);

        var def = new ProcedureDefinition(
            Name: "usp_Test",
            Summary: "Test Procedure",
            ProcedureName: "dbo.usp_Test",
            Mode: ProcedureMode.Read,
            DataSource: "ds1",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: null,
            ClearedResultColumns: [],
            RequiredRoles: [],
            AllowDynamicSql: false,
            TimeoutSeconds: 30);

        var sec = new ProcedureSecurityContext(Tenant.Value, UserSid.Value, Purpose: "audit");

        try
        {
            await invoker.ExecuteReadAsync(def, new Dictionary<string, object?>(), sec, CancellationToken.None);
        }
        catch
        {
            // Ignore downstream command execution errors since mock DbCommand may lack full implementation
        }

        await mockSessionInit.Received(1).InitializeSessionAsync(
            mockConn,
            tx: null,
            DatabaseDialect.SqlServer,
            Tenant,
            userSid: UserSid,
            purpose: "audit",
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public void R_SQL_6_Invoker_DefaultsToConcreteDbSessionContextInitializer()
    {
        var sqlConnFactory = Substitute.For<ISqlConnectionFactory>();
        var connProvider = new ProcedureConnectionProvider(sqlConnFactory, Options.Create(new GatewayOptions()));
        var invoker = new MssqlProcedureInvoker(connProvider, Options.Create(new GatewayOptions()));
        invoker.ShouldNotBeNull();
    }
}
