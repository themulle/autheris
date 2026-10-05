namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: Loads <c>*.proc.sql</c> declarations into the <see cref="IProcedureRegistry"/> (state Pending).
/// Activation happens only after <see cref="StoredProcedureCatalogValidator"/> accepted the declaration.
/// </summary>
public sealed class ProcedureDefinitionLoader : IDisposable
{
    internal const string FilePattern = "*.proc.sql";
    private const long MaxFileBytes = 64 * 1024;

    private readonly IProcedureRegistry _registry;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<ProcedureDefinitionLoader>? _logger;
    private readonly ConcurrentDictionary<string, string> _fileToName = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public ProcedureDefinitionLoader(
        IProcedureRegistry registry,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null,
        ILogger<ProcedureDefinitionLoader>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment;
        _logger = logger;
    }

    private ProcedureEndpointsOptions Settings => _options.Value.SqlEndpoints.Procedures;

    private bool AllowRlsNone => _environment?.IsDevelopment() == true;

    /// <summary>Loads all declarations of the configured directory. Returns the number of registered procedures.</summary>
    public int LoadFromDirectory()
    {
        string directory = Settings.Directory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            _logger?.LogInformation("Procedure directory '{Directory}' does not exist; no stored procedure endpoints loaded.", directory);
            return 0;
        }

        int count = 0;
        foreach (string file in Directory.GetFiles(directory, FilePattern, SearchOption.AllDirectories))
        {
            if (TryLoadFile(file, directory))
            {
                count++;
            }
        }

        if (Settings.EnableHotReload && _watcher == null)
        {
            SetupWatcher(directory);
        }

        _logger?.LogInformation("Loaded {Count} stored procedure declaration(s) from '{Directory}' (pending validation).", count, directory);
        return count;
    }

    /// <summary>Parses and registers a single declaration file. Failures are logged and the file is skipped.</summary>
    public bool TryLoadFile(string filePath, string rootDirectory)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return false;
            }

            // Symlinks / reparse points are never followed; the file must live below the configured directory.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null)
            {
                _logger?.LogWarning("Procedure declaration '{File}' skipped: symbolic links are not allowed.", filePath);
                ForgetFile(filePath);
                return false;
            }

            string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(filePath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Procedure declaration '{File}' skipped: outside of the procedure directory.", filePath);
                ForgetFile(filePath);
                return false;
            }

            if (info.Length > MaxFileBytes)
            {
                _logger?.LogWarning("Procedure declaration '{File}' skipped: file exceeds {Max} bytes.", filePath, MaxFileBytes);
                ForgetFile(filePath);
                return false;
            }

            string defaultName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
            var definition = ProcedureDefinitionParser.Parse(
                File.ReadAllText(filePath), defaultName, AllowRlsNone, Settings.MaxTimeoutSeconds);

            string? rejection = CheckPolicy(definition);
            if (rejection != null)
            {
                _logger?.LogWarning("Procedure declaration '{File}' rejected: {Reason}", filePath, rejection);
                _registry.Unregister(definition.Name);
                ForgetFile(filePath);
                return false;
            }

            _registry.Register(definition);
            _fileToName[Path.GetFullPath(filePath)] = definition.Name;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException or RegexMatchTimeoutException)
        {
            _logger?.LogWarning(ex, "Procedure declaration '{File}' is invalid and was skipped.", filePath);
            ForgetFile(filePath);
            return false;
        }
    }

    /// <summary>Static policy checks that need no database access.</summary>
    internal string? CheckPolicy(ProcedureDefinition definition)
    {
        if (definition.Mode == ProcedureMode.Write)
        {
            return "write procedures are not supported yet (phase 2).";
        }

        var match = definition.ProcedureName.Split('.');
        string schema = match[0];
        string proc = match[1];

        if (!Settings.AllowedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
        {
            return $"schema '{schema}' is not listed in SqlEndpoints.Procedures.AllowedSchemas.";
        }

        if (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
            schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
            proc.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
            proc.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
        {
            return "system procedures (sys.*, sp_*, xp_*) cannot be exposed.";
        }

        if (!string.IsNullOrWhiteSpace(definition.DataSource) &&
            !string.Equals(definition.DataSource, Settings.ConnectionName, StringComparison.OrdinalIgnoreCase) &&
            !Settings.AllowedDataSources.Contains(definition.DataSource, StringComparer.OrdinalIgnoreCase))
        {
            return $"data source '{definition.DataSource}' is not allowed for procedures.";
        }

        return null;
    }

    /// <summary>Fail-closed: a deleted or no longer valid declaration is removed from the registry.</summary>
    private void ForgetFile(string filePath)
    {
        if (_fileToName.TryRemove(Path.GetFullPath(filePath), out var name))
        {
            _registry.Unregister(name);
        }
    }

    private void SetupWatcher(string directory)
    {
        try
        {
            _watcher = new FileSystemWatcher(directory, FilePattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Created += (_, e) => TryLoadFile(e.FullPath, directory);
            _watcher.Changed += (_, e) => TryLoadFile(e.FullPath, directory);
            _watcher.Deleted += (_, e) => ForgetFile(e.FullPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger?.LogWarning(ex, "Could not start the file watcher on '{Directory}'. Hot reload disabled.", directory);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }
}
