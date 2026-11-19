namespace Autheris.Tests.Integration.Interop.Trino;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>
/// The official Trino JDBC driver (pinned jar, checksum verified by the nightly workflow) against a live gateway on HTTPS loopback.
/// Run: AUTHERIS_TRINO_JDBC_JAR=/path/trino-jdbc.jar dotnet test tests/Autheris.Tests.Integration --filter "Category=TrinoInterop".
/// </summary>
[Trait("Category", "TrinoInterop")]
public sealed class TrinoJdbcInteropTests : IClassFixture<TrinoInteropGatewayFixture>
{
    private const string Probe = """
        import java.sql.*;
        import java.util.*;

        public class Probe {
            public static void main(String[] a) throws Exception {
                String mode = a[0];
                Properties p = new Properties();
                p.setProperty("user", "interop");
                p.setProperty("accessToken", System.getenv("TRINO_TOKEN"));
                p.setProperty("SSL", "true");
                p.setProperty("SSLVerification", "NONE");
                try (Connection c = DriverManager.getConnection(a[1], p)) {
                    if (mode.equals("select")) {
                        try (Statement s = c.createStatement(); ResultSet rs = s.executeQuery(a[2])) {
                            ResultSetMetaData md = rs.getMetaData();
                            StringBuilder names = new StringBuilder();
                            for (int i = 1; i <= md.getColumnCount(); i++) { names.append(i > 1 ? "," : "").append(md.getColumnLabel(i)); }
                            System.out.println("COLUMNS " + names);
                            int n = 0;
                            String first = null;
                            while (rs.next()) {
                                if (n == 0) {
                                    StringBuilder row = new StringBuilder();
                                    for (int i = 1; i <= md.getColumnCount(); i++) { row.append(i > 1 ? "|" : "").append(rs.getString(i)); }
                                    first = row.toString();
                                }
                                n++;
                            }
                            System.out.println("COUNT " + n);
                            System.out.println("FIRST " + first);
                        }
                    } else if (mode.equals("cancel")) {
                        try (Statement s = c.createStatement(); ResultSet rs = s.executeQuery(a[2])) {
                            rs.next();
                            s.cancel();
                            System.out.println("CANCELED");
                        }
                    } else if (mode.equals("meta")) {
                        DatabaseMetaData md = c.getMetaData();
                        try (ResultSet rs = md.getCatalogs()) { while (rs.next()) { System.out.println("CATALOG " + rs.getString(1)); } }
                        try (ResultSet rs = md.getSchemas("default", null)) { while (rs.next()) { System.out.println("SCHEMA " + rs.getString(1)); } }
                        try (ResultSet rs = md.getTables("default", "main", null, null)) { while (rs.next()) { System.out.println("TABLE " + rs.getString("TABLE_NAME")); } }
                        try (ResultSet rs = md.getColumns("default", "main", "big_t", null)) { while (rs.next()) { System.out.println("COLUMN " + rs.getString("COLUMN_NAME")); } }
                    }
                } catch (SQLException e) {
                    System.out.println("ERROR code=" + e.getErrorCode() + " msg=" + e.getMessage());
                }
            }
        }
        """;

    private readonly TrinoInteropGatewayFixture _fixture;

    public TrinoJdbcInteropTests(TrinoInteropGatewayFixture fixture) => _fixture = fixture;

    private async Task<IReadOnlyList<string>> RunAsync(string token, string mode, string? sql = null)
    {
        var jar = TrinoInteropProcess.Require(TrinoInteropProcess.JdbcJarVariable);
        var script = TrinoInteropProcess.WriteScript("Probe.java", Probe);
        var args = new List<string> { "-cp", jar, script, mode, $"jdbc:trino://127.0.0.1:{_fixture.Port}/default" };
        if (sql is not null)
        {
            args.Add(sql);
        }

        var (exit, output, error) = await TrinoInteropProcess.RunAsync("java", args, new Dictionary<string, string> { ["TRINO_TOKEN"] = token });
        exit.ShouldBe(0, error + output);
        return output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
    }

    [Fact]
    public async Task Jdbc_SelectSmall_ReturnsRows()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "select", "SELECT id, label FROM default.main.big_t WHERE id <= 5 ORDER BY id");

        lines.ShouldContain("COLUMNS id,label", string.Join('\n', lines));
        lines.ShouldContain("COUNT 5");
        lines.ShouldContain("FIRST 1|label-1");
    }

    [Fact]
    public async Task Jdbc_SelectLarge_PagesAllRows()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "select", "SELECT id, label FROM default.main.big_t");

        lines.ShouldContain($"COUNT {TrinoInteropGatewayFixture.LargeRowCount}", string.Join('\n', lines));
    }

    [Fact]
    public async Task Jdbc_MaskedColumn_IsMasked()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "select", "SELECT id, secret FROM default.main.big_t WHERE id <= 3 ORDER BY id");

        var first = lines.Single(l => l.StartsWith("FIRST ", System.StringComparison.Ordinal));
        first.ShouldStartWith("FIRST 1|");
        first.ShouldNotContain("secret-1");
    }

    [Fact]
    public async Task Jdbc_DeniedTable_RaisesPermissionDenied()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "select", "SELECT id FROM " + TrinoInteropGatewayFixture.DeniedTable);

        var error = lines.Single(l => l.StartsWith("ERROR ", System.StringComparison.Ordinal));
        error.ShouldStartWith("ERROR code=4 ");
        error.ShouldNotContain("vault_t");
        error.ShouldNotContain("closed");
    }

    [Fact]
    public async Task Jdbc_Cancel_StopsStatement()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "cancel", "SELECT id, label FROM default.main.big_t");

        lines.ShouldContain("CANCELED", string.Join('\n', lines));
    }

    [Fact]
    public async Task Jdbc_DatabaseMetaData_GetCatalogsSchemasTablesColumns()
    {
        var lines = await RunAsync(TrinoInteropGatewayFixture.ReaderToken, "meta");

        lines.ShouldContain("CATALOG default");
        lines.ShouldContain("SCHEMA main");
        lines.ShouldContain("TABLE big_t");
        lines.ShouldContain("COLUMN id");
        lines.ShouldContain("COLUMN secret");
    }

    [Fact]
    public async Task Jdbc_ReadOnlyToken_QueriesAndCancels()
    {
        var select = await RunAsync(TrinoInteropGatewayFixture.ReadOnlyToken, "select", "SELECT id FROM default.main.big_t WHERE id <= 5");
        select.ShouldContain("COUNT 5", string.Join('\n', select));

        var cancel = await RunAsync(TrinoInteropGatewayFixture.ReadOnlyToken, "cancel", "SELECT id, label FROM default.main.big_t");
        cancel.ShouldContain("CANCELED", string.Join('\n', cancel));
    }
}
