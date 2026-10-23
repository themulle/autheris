namespace Autheris.Tests.Parity.Matrix;

public enum MatrixChannel
{
    WebSql,
    GraphQl,
    Rest,
    Mcp,
}

/// <summary>How a channel is asked for a capability: WebSQL text, a GraphQL document or a REST path and query.</summary>
public abstract record MatrixRequest;

public sealed record SqlRequest(string Sql, object? Parameters = null) : MatrixRequest;

public sealed record GraphQlRequest(string Query, string Field, object? Variables = null) : MatrixRequest;

public sealed record RestRequest(string PathAndQuery) : MatrixRequest;

/// <summary>
/// Expected result of a capability: the columns to compare (names as the channel returns them, see <see cref="Supported.ActualColumns"/>)
/// and the rows computed by <see cref="ParityOracle"/>; order matters only when <see cref="Ordered"/>.
/// </summary>
public sealed record ExpectedSpec(string[] Columns, object?[][] Rows, bool Ordered = false);

/// <summary>One cell of the capability x channel matrix: supported (result asserted), unsupported (stable 4xx asserted) or pending.</summary>
public abstract record MatrixCell;

/// <summary><paramref name="ActualColumns"/> renames the compared columns when the channel names them differently (for example a GraphQL group key).</summary>
public sealed record Supported(MatrixRequest Request, ExpectedSpec Expected, string[]? ActualColumns = null) : MatrixCell;

/// <summary>
/// A capability the channel rejects: HTTP 4xx with a stable code (<paramref name="Code"/> exactly, or any catalogue code when null) and an
/// English message that matches <paramref name="NamesCapability"/> when given.
/// </summary>
public sealed record Unsupported(MatrixRequest Request, string? Code, string? NamesCapability = null) : MatrixCell;

/// <summary>Registered, counted and reported, not failing (PRD block A.1): the channel is not implemented in the parity host.</summary>
public sealed record Pending(string Reason) : MatrixCell;
