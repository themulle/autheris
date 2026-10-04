namespace Autheris.Domain.Model;

public sealed record PurgeResult(bool Success, int PurgedCount, string? ErrorMessage = null);

public sealed record CacheTagDescriptor(IReadOnlyList<string> TypeTags, IReadOnlyList<string> EntityTags);
