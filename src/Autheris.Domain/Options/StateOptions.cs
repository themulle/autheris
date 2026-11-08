namespace Autheris.Domain.Options;

/// <summary>
/// <c>Gateway:State</c>the writable directory for local gateway state. By default it holds the audit chain anchor. A relative path is resolved against the
/// working directory; in a container it must point to a mounted volume (for example <c>/app/data</c>).
/// </summary>
public sealed class StateOptions
{
    public const string SectionName = "Gateway:State";

    public string StateDirectory { get; set; } = "./state";
}
