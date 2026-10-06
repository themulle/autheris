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
    internal const string FilePattern = "*.proc.*";
    private const long MaxFileBytes = 64 * 1024;

    internal static bool IsSupportedProcedureFile(string filePath) =>
        filePath.EndsWith(".proc.sql", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".proc.yaml", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".proc.yml", StringComparison.OrdinalIgnoreCase);

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
        foreach (string file in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (IsSupportedProcedureFile(file) && TryLoadFile(file, directory))
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

            // Symlinks / reparse points are never followed (file or any directory below the root, review P-8);
            // the file must live below the configured directory.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null || HasLinkedDirectory(info, rootDirectory))
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
            string content = File.ReadAllText(filePath);
            var definition = (filePath.EndsWith(".proc.yaml", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".proc.yml", StringComparison.OrdinalIgnoreCase))
                ? ProcedureDefinitionParser.ParseYaml(content, defaultName, AllowRlsNone, Settings.MaxTimeoutSeconds)
                : ProcedureDefinitionParser.Parse(content, defaultName, AllowRlsNone, Settings.MaxTimeoutSeconds);

            string fullPath = Path.GetFullPath(filePath);
            string? rejection = CheckPolicy(definition);
            if (rejection == null)
            {
                // Review P-8: two files must not declare the same endpoint (silent shadowing).
                var owner = _fileToName.FirstOrDefault(kv =>
                    string.Equals(kv.Value, definition.Name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kv.Key, fullPath, StringComparison.OrdinalIgnoreCase));
                if (owner.Key != null)
                {
                    rejection = $"endpoint name '{definition.Name}' is already declared in '{Path.GetFileName(owner.Key)}'.";
                }
            }

            if (rejection != null)
            {
                _logger?.LogWarning("Procedure declaration '{File}' rejected: {Reason}", filePath, rejection);
                ForgetFile(filePath);
                return false;
            }

            // Review P-8: if the endpoint name inside this file changed, the old endpoint must disappear.
            if (_fileToName.TryGetValue(fullPath, out var previousName) &&
                !string.Equals(previousName, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                _registry.Unregister(previousName);
            }

            _registry.Register(definition);
            _fileToName[fullPath] = definition.Name;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Review P-3: every parse error (incl. YamlException, NullReferenceException) only skips this file;
            // it must never stop the host or crash the file watcher thread. The endpoint is removed (fail-closed).
            _logger?.LogWarning(ex, "Procedure declaration '{File}' is invalid and was skipped.", filePath);
            SafeForget(filePath);
            return false;
        }
    }

    private static bool HasLinkedDirectory(FileInfo file, string rootDirectory)
    {
        string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = file.Directory; dir != null; dir = dir.Parent)
        {
            if (string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget != null)
            {
                return true;
            }
        }

        return false;
    }

    private void SafeForget(string filePath)
    {
        try
        {
            ForgetFile(filePath);

            // Review P-8: a deleted or renamed directory raises no events for the files below it; all declarations
            // loaded from that subtree are switched off (fail-closed).
            string prefix = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var key in _fileToName.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                ForgetFile(key);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger?.LogDebug(ex, "Could not resolve '{File}' while unregistering it.", filePath);
        }
    }

    /// <summary>Static policy checks that need no database access.</summary>
    internal string? CheckPolicy(ProcedureDefinition definition)
    {
        if (definition.Mode == ProcedureMode.Write)
        {
            return "write procedures are not supported yet (phase 2).";
        }

        // Review P-1: declared mode skips the catalog validation; it is opt-in outside Development and always needs a
        // result table plus explicit outputs so consent column rules can be applied.
        if (definition.ValidationMode == ProcedureValidationMode.Declared)
        {
            if (_environment?.IsDevelopment() != true && !Settings.AllowDeclaredValidation)
            {
                return "'validation: declared' is only allowed in Development or with SqlEndpoints.Procedures.AllowDeclaredValidation=true.";
            }

            if (string.IsNullOrWhiteSpace(definition.ResultTable) || definition.DeclaredOutputs.Count == 0)
            {
                return "'validation: declared' requires result_table and a non-empty list of outputs.";
            }
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
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            // Review P-3/P-8: handlers run on thread-pool threads; an exception there would terminate the process.
            _watcher.Created += (_, e) => OnWatcherEvent(() =>
            {
                if (IsSupportedProcedureFile(e.FullPath))
                {
                    TryLoadFile(e.FullPath, directory);
                }
                else if (Directory.Exists(e.FullPath))
                {
                    foreach (var f in Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories).Where(IsSupportedProcedureFile))
                    {
                        TryLoadFile(f, directory);
                    }
                }
            });
            _watcher.Changed += (_, e) => OnWatcherEvent(() => { if (IsSupportedProcedureFile(e.FullPath)) TryLoadFile(e.FullPath, directory); });
            _watcher.Deleted += (_, e) => OnWatcherEvent(() => SafeForget(e.FullPath));
            _watcher.Renamed += (_, e) => OnWatcherEvent(() =>
            {
                // Renaming x.proc.sql to x.proc.sql.disabled must switch the endpoint off; atomic saves (temp + rename)
                // must load the new content.
                SafeForget(e.OldFullPath);
                if (IsSupportedProcedureFile(e.FullPath))
                {
                    TryLoadFile(e.FullPath, directory);
                }
                else if (Directory.Exists(e.FullPath))
                {
                    // A directory moved into the watched tree: load its declarations.
                    foreach (var f in Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories).Where(IsSupportedProcedureFile))
                    {
                        TryLoadFile(f, directory);
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger?.LogWarning(ex, "Could not start the file watcher on '{Directory}'. Hot reload disabled.", directory);
        }
    }

    private void OnWatcherEvent(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Procedure hot reload failed.");
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
