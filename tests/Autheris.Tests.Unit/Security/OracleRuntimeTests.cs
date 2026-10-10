namespace Autheris.Tests.Unit.Security;

using System.Security;
using System.Text.RegularExpressions;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>Oracle runtime hardening (WP-F1, F2, F3, F4; SEC-ADG-03, -14, -24).</summary>
public sealed class OracleRuntimeTests
{
    private const string Tcps = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=APP)))";

    [Theory]
    [InlineData("User Id=app;Password=x;DBA Privilege=SYSDBA;Data Source=" + Tcps)]
    [InlineData("User Id=app;Password=x;DBA Privilege=SYSOPER;Data Source=" + Tcps)]
    [InlineData("User Id=app;Password=x;DBA Privilege=SYSASM;Data Source=" + Tcps)]
    [InlineData("User Id=/;Data Source=" + Tcps)]                                  // OS authentication
    [InlineData("Password=x;Data Source=" + Tcps)]                                 // no user
    [InlineData("User Id=app;Password=x;Proxy User Id=other;Data Source=" + Tcps)]
    [InlineData("User Id=app;Password=x;Proxy Password=p;Data Source=" + Tcps)]
    [InlineData("User Id=SYS;Password=x;Data Source=" + Tcps)]
    [InlineData("User Id=SYSTEM;Password=x;Data Source=" + Tcps)]
    public void OracleConnectionString_RejectsPrivilegedLogin(string connectionString)
    {
        Should.Throw<SecurityException>(() => OracleConnectionStringPolicy.Validate(connectionString, requireTcps: false));
    }

    [Fact]
    public void OracleConnectionString_AcceptsAnOrdinaryAccount()
    {
        Should.NotThrow(() => OracleConnectionStringPolicy.Validate("User Id=app;Password=x;Data Source=" + Tcps, requireTcps: true));
    }

    [Theory]
    [InlineData("User Id=app;Password=x;Data Source=db.example:1521/APP")]
    [InlineData("User Id=app;Password=x;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=APP)))")]
    public void OracleConnectionString_RequiresTcpsOutsideDevelopment(string connectionString)
    {
        Should.Throw<SecurityException>(() => OracleConnectionStringPolicy.Validate(connectionString, requireTcps: true));
        Should.NotThrow(() => OracleConnectionStringPolicy.Validate(connectionString, requireTcps: false));
    }

    [Theory]
    [InlineData("tcps://db.example:2484/APP")]
    [InlineData("TCPS://db.example:2484/APP")]
    public void OracleConnectionString_AcceptsTcpsEasyConnect(string dataSource)
    {
        Should.NotThrow(() => OracleConnectionStringPolicy.Validate($"User Id=app;Password=x;Data Source={dataSource}", requireTcps: true));
    }

    [Fact]
    public async Task ConnectionFactory_Oracle_ValidatesBeforeConnecting()
    {
        var factory = new SqlConnectionFactory();
        var options = new DataSourceConnectionOptions
        {
            Provider = "Oracle",
            ConnectionString = "User Id=app;Password=x;DBA Privilege=SYSDBA;Data Source=" + Tcps
        };
        await Should.ThrowAsync<SecurityException>(() => factory.CreateOpenConnectionAsync(options));
    }

    [Fact]
    public void Oracle_IsAnExecutableDialect() => SqlDialectMapper.IsExecutable(DatabaseDialect.Oracle).ShouldBeTrue();

    [Theory]
    [InlineData(DatabaseDialect.Oracle, "gql_limit", ":gql_limit", "gql_limit")]
    [InlineData(DatabaseDialect.SqlServer, "gql_limit", "@gql_limit", "@gql_limit")]
    [InlineData(DatabaseDialect.PostgreSql, "gql_limit", "@gql_limit", "@gql_limit")]
    [InlineData(DatabaseDialect.Sqlite, "gql_limit", "@gql_limit", "@gql_limit")]
    public void ParameterMarker_IsDialectAware(DatabaseDialect dialect, string name, string marker, string parameterName)
    {
        dialect.FormatParameterMarker(name).ShouldBe(marker);
        dialect.FormatParameterName(name).ShouldBe(parameterName);
    }

    [Fact]
    public void OracleSession_PinsNlsAndVerifiesThem_WithoutAnyRequestData()
    {
        string sql = OracleSessionInitialization.PinAndVerifyBlock;
        foreach (var setting in new[]
                 {
                     "NLS_COMP = ''BINARY''", "NLS_SORT = ''BINARY''", "NLS_LANGUAGE = ''AMERICAN''", "NLS_TERRITORY = ''AMERICA''",
                     "NLS_NUMERIC_CHARACTERS = ''.,''", "NLS_DATE_FORMAT = ''YYYY-MM-DD''", "TIME_ZONE = ''+00:00''"
                 })
        {
            sql.ShouldContain(setting);
        }

        sql.ShouldContain("DBMS_SESSION.CLEAR_IDENTIFIER");
        sql.ShouldContain("RAISE_APPLICATION_ERROR");
        sql.ShouldContain("SYS_CONTEXT('USERENV', 'NLS_COMP')");
        sql.ShouldNotContain("@");
    }

    [Fact]
    public void NoHardcodedAtMarkers_OnGovernedPaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var literalMarker = new Regex("\\$?\"@[A-Za-z_{]|\"@\"\\s*\\+", RegexOptions.Compiled);
        foreach (var relative in new[]
                 {
                     "src/Autheris.Application/Services/SqlDataSourceExecutor.cs",
                     "src/Autheris.Application/Sql/Masking/SqlDataMaskingProvider.cs"
                 })
        {
            var offenders = File.ReadLines(Path.Combine(dir!.FullName, relative))
                .Select((line, i) => (line, i))
                .Where(x => literalMarker.IsMatch(x.line))
                .Select(x => $"{relative}:{x.i + 1}")
                .ToList();
            offenders.ShouldBeEmpty();
        }
    }
}
