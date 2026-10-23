using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Autheris.Tests.Parity.Robin;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Matrix;

/// <summary>Runs one matrix cell against a host (any dialect) through <see cref="RobinClient"/>, so every response passes the no-5xx/no-leak invariant.</summary>
public sealed class MatrixExecutor(IParityHost host, string schema = "main")
{
    private readonly RobinClient _client = new(host);

    public async Task<ChannelResult> ExecuteAsync(MatrixRequest request) => ForSchema(request, schema) switch
    {
        SqlRequest s => await _client.WebSqlAsync(s.Sql, s.Parameters),
        GraphQlRequest g => await _client.GraphQlAsync(g.Query, g.Field, g.Variables),
        RestRequest r => await _client.RestAsync(r.PathAndQuery),
        _ => throw new InvalidOperationException("Unknown request kind."),
    };

    /// <summary>The matrix is written for the SQLite schema <c>main</c>; GraphQL field names and REST paths carry the schema of the dialect host.</summary>
    public static MatrixRequest ForSchema(MatrixRequest request, string schema) => schema == "main"
        ? request
        : request switch
        {
            GraphQlRequest g => g with { Query = g.Query.Replace("default_main_", $"default_{schema}_", StringComparison.Ordinal), Field = g.Field.Replace("default_main_", $"default_{schema}_", StringComparison.Ordinal) },
            RestRequest r => r with { PathAndQuery = r.PathAndQuery.Replace("/default/main/", $"/default/{schema}/", StringComparison.Ordinal) },
            _ => request,
        };

    public async Task RunCellAsync(string capability, MatrixChannel channel)
    {
        var cell = ParityMatrix.Cells[(capability, channel)];
        switch (cell)
        {
            case Pending:
                throw new PendingCellException(channel.ToString(), capability);

            case Supported s:
                var ok = await ExecuteAsync(s.Request);
                ok.RowsShouldBe(s.ActualColumns ?? s.Expected.Columns, s.Expected.Rows, s.Expected.Ordered);
                break;

            case Unsupported u:
                var rejected = await ExecuteAsync(u.Request);
                rejected.Rejected(u.Code);
                if (u.NamesCapability is not null && !Regex.IsMatch(rejected.RawBody, u.NamesCapability))
                {
                    throw new XunitException($"The rejection does not name the capability ({u.NamesCapability}): {RobinAssert.Snippet(rejected.RawBody)}");
                }

                break;

            default:
                throw new InvalidOperationException("Unknown matrix cell kind.");
        }
    }
}
