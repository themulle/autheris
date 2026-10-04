namespace Autheris.Domain.Options;

public sealed class CdnOptions
{
    public bool Enabled { get; init; } = false;
    public string PurgeMode { get; init; } = "Tags"; // "Tags" | "Urls" | "All"
    public CloudflareOptions Cloudflare { get; init; } = new();
    public FastlyOptions Fastly { get; init; } = new();
}

public sealed class CloudflareOptions
{
    public bool Enabled { get; init; } = false;
    public string ApiToken { get; init; } = string.Empty;
    public string ZoneId { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.cloudflare.com/client/v4/";
}

public sealed class FastlyOptions
{
    public bool Enabled { get; init; } = false;
    public string ApiKey { get; init; } = string.Empty;
    public string ServiceId { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.fastly.com/";
}
