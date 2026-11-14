namespace Autheris.Tests.Common.Hosting;

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Issue #520 (WP-OI-CI-3a): Shared reloadable <see cref="ConfigurationProvider"/> for integration tests
/// that mutate <c>IOptionsMonitor&lt;T&gt;</c>-backed settings between test cases without rebuilding a full
/// <c>WebApplicationFactory&lt;Program&gt;</c> host.
/// </summary>
public sealed class ReloadableConfig : ConfigurationProvider
{
    public void Put(string key, string? value)
    {
        Data[key] = value;
        OnReload();
    }

    public void PutMany(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        foreach (var (key, value) in entries)
        {
            Data[key] = value;
        }

        OnReload();
    }
}

/// <summary>
/// <see cref="IConfigurationSource"/> that wraps an existing <see cref="ReloadableConfig"/> instance.
/// </summary>
public sealed class ReloadableConfigSource(ReloadableConfig provider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
}
