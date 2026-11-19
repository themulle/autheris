namespace Autheris.Tests.Integration.Interop.Trino;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>
/// The official trino-python-client (pinned with hashes by <c>requirements-trino-interop.txt</c>) against a live gateway on HTTPS loopback.
/// Run: AUTHERIS_TRINO_PYTHON=/path/to/venv/bin/python dotnet test tests/Autheris.Tests.Integration --filter "Category=TrinoInterop".
/// </summary>
[Trait("Category", "TrinoInterop")]
public sealed class TrinoPythonInteropTests : IClassFixture<TrinoInteropGatewayFixture>
{
    private const string Probe = """
        import os
        import sys

        import trino
        from trino.auth import JWTAuthentication

        mode = sys.argv[1]
        port = int(sys.argv[2])
        auth = JWTAuthentication(os.environ["TRINO_TOKEN"])


        def connect():
            return trino.dbapi.connect(host="127.0.0.1", port=port, user="interop", catalog="default", schema="main",
                                       http_scheme="https", auth=auth, verify=False)


        if mode == "fetchall":
            cur = connect().cursor()
            cur.execute(sys.argv[3])
            rows = cur.fetchall()
            print("COUNT", len(rows))
            print("FIRST", "|".join(str(v) for v in rows[0]) if rows else "")
        elif mode == "inspect":
            from sqlalchemy import create_engine, inspect
            from trino.sqlalchemy import URL

            engine = create_engine(URL(host="127.0.0.1", port=port, user="interop", catalog="default", schema="main"),
                                   connect_args={"http_scheme": "https", "auth": auth, "verify": False})
            inspector = inspect(engine)
            for schema in inspector.get_schema_names():
                print("SCHEMA", schema)
            for table in inspector.get_table_names("main"):
                print("TABLE", table)
            for column in inspector.get_columns("big_t", "main"):
                print("COLUMN", column["name"])
        elif mode == "error":
            try:
                cur = connect().cursor()
                cur.execute(sys.argv[3])
                cur.fetchall()
                print("NOERROR")
            except trino.exceptions.TrinoUserError as e:
                print("USERERROR", e.error_code, e.error_name, e.error_type)
                print("MESSAGE", e.message)
        """;

    private readonly TrinoInteropGatewayFixture _fixture;

    public TrinoPythonInteropTests(TrinoInteropGatewayFixture fixture) => _fixture = fixture;

    private async Task<IReadOnlyList<string>> RunAsync(string mode, string? sql = null)
    {
        var python = TrinoInteropProcess.Require(TrinoInteropProcess.PythonVariable);
        var script = TrinoInteropProcess.WriteScript("probe.py", Probe);
        var args = new List<string> { "-I", script, mode, _fixture.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (sql is not null)
        {
            args.Add(sql);
        }

        var environment = new Dictionary<string, string> { ["TRINO_TOKEN"] = TrinoInteropGatewayFixture.ReaderToken };
        var (exit, output, error) = await TrinoInteropProcess.RunAsync(python, args, environment);
        exit.ShouldBe(0, error + output);
        return output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
    }

    [Fact]
    public async Task Python_FetchAll_Paged()
    {
        var lines = await RunAsync("fetchall", "SELECT id, label FROM default.main.big_t");

        lines.ShouldContain($"COUNT {TrinoInteropGatewayFixture.LargeRowCount}", string.Join('\n', lines));
    }

    [Fact]
    public async Task Python_SqlAlchemyInspector_ListsSchemasTablesColumns()
    {
        var lines = await RunAsync("inspect");

        lines.ShouldContain("SCHEMA main", string.Join('\n', lines));
        lines.ShouldContain("TABLE big_t");
        lines.ShouldContain("COLUMN id");
        lines.ShouldContain("COLUMN secret");
    }

    [Fact]
    public async Task Python_ErrorShape_IsTrinoUserError()
    {
        var lines = await RunAsync("error", "SELECT id FROM " + TrinoInteropGatewayFixture.DeniedTable);

        var error = lines.Single(l => l.StartsWith("USERERROR ", System.StringComparison.Ordinal));
        error.ShouldBe("USERERROR 4 PERMISSION_DENIED USER_ERROR");
        string.Join('\n', lines).ShouldNotContain("vault_t");
    }
}
