namespace Autheris.Domain.Interfaces;

using Autheris.Domain.Model;

/// <summary>Issue #111 (A-10): visibility given to a table at creation. Applied only in the insert branch of the metadata upsert, never at read time.</summary>
public interface ICatalogVisibilityDefaults
{
    DatasetVisibility NewTableDefault { get; }
}
