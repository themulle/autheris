using System.Globalization;

namespace Autheris.Sim.ApiHost;

public enum SimApiProfile
{
    Read,
    Write,
}

/// <summary>Thrown for any invalid or unsafe host configuration. The host exits non-zero before it binds anything (fail-closed).</summary>
public sealed class SimApiHostConfigurationException(string message) : Exception(message);

/// <summary>Validated command line of <c>Autheris.Sim.ApiHost</c>. Pure (no ASP.NET Core types) so the Simulation Meta tests can link and test it.</summary>
public sealed record SimApiHostOptions(int Port, string DataDir, string? ReadyFile, SimApiProfile Profile)
{
    public const int DefaultPort = 5071;

    public const int MinPort = 1024;

    public const int MaxPort = 65535;

    private static readonly string[] UrlEnvironmentVariables = ["ASPNETCORE_URLS", "DOTNET_URLS", "URLS", "Urls"];

    private static readonly string[] PortEnvironmentVariables = ["ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS", "HTTP_PORTS", "HTTPS_PORTS"];

    /// <summary>Exactly <c>127.0.0.1</c> or <c>localhost</c>; never a prefix match, never userinfo, never a wildcard (SEC-SIM-01).</summary>
    public static bool IsLoopbackHost(string? host) =>
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses an absolute http(s) URL and accepts it only when its host is loopback and it carries no userinfo.</summary>
    public static bool IsLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('@', StringComparison.Ordinal) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               IsLoopbackHost(uri.Host);
    }

    public static SimApiHostOptions Parse(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var port = DefaultPort;
        string? dataDir = null;
        string? readyFile = null;
        var profile = SimApiProfile.Read;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (!seen.Add(name))
            {
                throw new SimApiHostConfigurationException($"Duplicate argument {name}.");
            }

            if (name is not ("--port" or "--data-dir" or "--ready-file" or "--profile" or "--bind"))
            {
                throw new SimApiHostConfigurationException($"Unknown argument {name}.");
            }

            if (i + 1 >= args.Count)
            {
                throw new SimApiHostConfigurationException($"Missing value for {name}.");
            }

            var value = args[++i];
            switch (name)
            {
                case "--port":
                    port = ParsePort(value);
                    break;
                case "--data-dir":
                    dataDir = value;
                    break;
                case "--ready-file":
                    readyFile = value;
                    break;
                case "--profile":
                    profile = value switch
                    {
                        "read" => SimApiProfile.Read,
                        "write" => SimApiProfile.Write,
                        _ => throw new SimApiHostConfigurationException("--profile must be read or write."),
                    };
                    break;
                case "--bind":
                    if (!IsLoopbackHost(value))
                    {
                        throw new SimApiHostConfigurationException("--bind must be exactly 127.0.0.1 or localhost; the sim host binds loopback only.");
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new SimApiHostConfigurationException("--data-dir is required.");
        }

        foreach (var variable in UrlEnvironmentVariables)
        {
            var urls = getEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(urls))
            {
                continue;
            }

            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Wildcards (http://+:80, http://*:80, http://0.0.0.0, http://[::]) are not loopback and fail here.
                if (!IsLoopbackUrl(url))
                {
                    throw new SimApiHostConfigurationException($"{variable} resolves to a non-loopback address; refusing to start.");
                }
            }
        }

        foreach (var variable in PortEnvironmentVariables)
        {
            // *_HTTP_PORTS binds every interface; the host sets its own loopback endpoint instead.
            if (!string.IsNullOrWhiteSpace(getEnvironmentVariable(variable)))
            {
                throw new SimApiHostConfigurationException($"{variable} binds all interfaces; unset it. The sim host binds 127.0.0.1 only.");
            }
        }

        return new SimApiHostOptions(port, dataDir, readyFile, profile);
    }

    private static int ParsePort(string value)
    {
        if (value.Length is 0 or > 5 || !value.All(char.IsAsciiDigit) ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < MinPort or > MaxPort)
        {
            throw new SimApiHostConfigurationException($"--port must be numeric between {MinPort} and {MaxPort}.");
        }

        return port;
    }
}
