namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: Loads <c>*.proc.yaml</c> declarations into the <see cref="IProcedureRegistry"/> (state Pending).
/// Activation happens only after <see cref="StoredProcedureCatalogValidator"/> accepted the declaration.
/// </summary>
public sealed class ProcedureDefinitionLoader
{
    internal const string LegacySqlHeaderMessage = "SQL-header procedure declarations (*.proc.sql) are no longer supported; convert them to *.proc.yaml.";
    internal const string WriteModeRejectedMessage = "Write procedures are enabled with data products (WP-SIMPL-49).";
    private const long MaxFileBytes = 64 * 1024;

    internal static bool IsSupportedProcedureFile(string filePath) =>
        filePath.EndsWith(".proc.yaml", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".proc.yml", StringComparison.OrdinalIgnoreCase);

    private readonly IProcedureRegistry _registry;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<ProcedureDefinitionLoader>? _logger;
    private readonly ConcurrentDictionary<string, string> _fileToName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _fileHash = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>INC-2 D-8: null resolves to required in every environment except exactly Development (fail-closed).</summary>
    private bool RequireDdlHash => Settings.RequireDdlHashForDeclared ?? _environment?.IsDevelopment() != true;

    private bool AllowRlsNone => _environment?.IsDevelopment() == true;

    /// <summary>Loads all declarations of the configured directory. Returns the number of registered procedures.</summary>
    public int LoadFromDirectory() => Scan(failOnLegacyFiles: true);

    /// <summary>
    /// Re-reads the configured directory (no other path): new or changed declarations are registered as Pending, files that
    /// disappeared or no longer load are removed, a <c>*.proc.sql</c> file is rejected and logged.
    /// </summary>
    public int Rescan() => Scan(failOnLegacyFiles: false);

    private int Scan(bool failOnLegacyFiles)
    {
        if (Settings.RequireDdlHashForDeclared == false && _environment?.IsDevelopment() != true)
        {
            _logger?.LogWarning("SqlEndpoints.Procedures.RequireDdlHashForDeclared is explicitly false outside Development; declared procedures without integrity.ddl_hash are accepted and drift is not detected.");
        }

        string directory = Settings.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                _logger?.LogInformation("Procedure directory '{Directory}' does not exist; no stored procedure endpoints loaded.", directory);
            }

            foreach (string known in _fileToName.Keys.ToList())
            {
                ForgetFile(known);
            }

            return 0;
        }

        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        string[] files = Directory.GetFiles(directory, "*.*", enumeration);
        if (files.Any(f => f.EndsWith(".proc.sql", StringComparison.OrdinalIgnoreCase)))
        {
            if (failOnLegacyFiles)
            {
                throw new InvalidOperationException(LegacySqlHeaderMessage);
            }

            _logger?.LogError("{Message}", LegacySqlHeaderMessage);
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string file in files.Where(IsSupportedProcedureFile))
        {
            present.Add(Path.GetFullPath(file));
            if (IsUnchanged(file) || TryLoadFile(file, directory))
            {
                count++;
            }
        }

        // a deleted, moved or linked file removes its entry.
        foreach (string known in _fileToName.Keys.Where(k => !present.Contains(k)).ToList())
        {
            ForgetFile(known);
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
            string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            var definition = ProcedureDefinitionParser.ParseYaml(content, defaultName, AllowRlsNone, Settings.MaxTimeoutSeconds);

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
            _fileHash[fullPath] = contentHash;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Review P-3: every parse error (incl. YamlException, NullReferenceException) only skips this file;
            // it must never stop the host. The endpoint is removed (fail-closed).
            _logger?.LogWarning(ex, "Procedure declaration '{File}' is invalid and was skipped.", filePath);
            ForgetFile(filePath);
            return false;
        }
    }

    /// <summary>An unchanged file that is still registered keeps its validated state instead of going back to Pending.</summary>
    private bool IsUnchanged(string file)
    {
        string full = Path.GetFullPath(file);
        try
        {
            return _fileHash.TryGetValue(full, out var hash) && _fileToName.TryGetValue(full, out var name) &&
                   _registry.TryGet(name, out _) &&
                   string.Equals(hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(file)))), StringComparison.Ordinal);
        }
        catch (IOException)
        {
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

    /// <summary>Static policy checks that need no database access.</summary>
    internal string? CheckPolicy(ProcedureDefinition definition)
    {
        if (definition.Mode == ProcedureMode.Write)
        {
            return WriteModeRejectedMessage;
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

            if (string.IsNullOrWhiteSpace(definition.DdlHash))
            {
                if (RequireDdlHash)
                {
                    return "declared mode requires integrity.ddl_hash";
                }

                _logger?.LogWarning("Procedure '{Procedure}' uses validation: declared without integrity.ddl_hash; drift is not detected.", definition.Name);
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

        // every loaded definition passes the same closed dialect admission as host start.
        string sourceName = string.IsNullOrWhiteSpace(definition.DataSource) ? Settings.ConnectionName : definition.DataSource;
        return ProcedureConnectionProvider.CheckAdmission(_options.Value, sourceName, definition.Kind);
    }

    /// <summary>Fail-closed: a deleted or no longer valid declaration is removed from the registry.</summary>
    private void ForgetFile(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        _fileHash.TryRemove(full, out _);
        if (_fileToName.TryRemove(full, out var name))
        {
            _registry.Unregister(name);
        }
    }
}
