namespace Autheris.Application.Docs.Interfaces;

/// <summary>
/// Indicates whether the initial load of the documentation search index has succeeded.
/// </summary>
public interface IDocIndexReadiness
{
    bool IsInitialLoadComplete { get; }
}
