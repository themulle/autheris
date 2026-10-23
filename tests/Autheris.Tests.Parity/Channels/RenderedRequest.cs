namespace Autheris.Tests.Parity.Channels;

/// <summary>An HTTP request a boundary channel renders. <see cref="SubmittedSql"/> lets the invariant check for echoed SQL.</summary>
public sealed record RenderedRequest(string Method, string PathAndQuery, string? Body = null, string ContentType = "application/json", string? SubmittedSql = null);
