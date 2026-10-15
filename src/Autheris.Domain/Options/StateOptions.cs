namespace Autheris.Domain.Options;

/// <summary>
/// <c>Gateway:State</c> (WP-ARC-B-13): the writable directory for local gateway state. With the SQLite governance provider it holds the
/// governance database (<c>governance.db</c>) and, by default, the audit chain anchor next to it. A relative path is resolved against the
/// working directory; in a container it must point to a mounted volume (for example <c>/app/data</c>).
/// </summary>
public sealed class StateOptions
{
    public const string SectionName = "Gateway:State";

    public string StateDirectory { get; set; } = "./state";
}
