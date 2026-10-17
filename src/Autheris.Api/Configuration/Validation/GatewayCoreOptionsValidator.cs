using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Validates core gateway options (row filter strategy, Casbin files, plugin manifest, and OpenMetadata HTTPS).
/// </summary>
public sealed class GatewayCoreOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly IFileSystemProbe _fileSystem;

    public GatewayCoreOptionsValidator(IHostEnvironment environment, IFileSystemProbe? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _fileSystem = fileSystem ?? PhysicalFileSystemProbe.Instance;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();

        if (!Enum.IsDefined(opts.RowFilters.SubqueryStrategy))
        {
            failures.Add("Gateway:RowFilters:SubqueryStrategy must be Exists, InCorrelated or In.");
        }

        // REQ-SQL-130 (BC-130-9): a time zone function without the closed zone set must not be admitted by configuration.
        if (opts.WebSql?.AdditionalAllowedFunctions is { Count: > 0 } additional &&
            additional.Any(TrinoSqlEngine.SqlFunctionAllowlists.IsTimeZoneFunctionName))
        {
            failures.Add("Gateway:WebSql:AdditionalAllowedFunctions must not list a time zone function; convert_timezone is the governed one.");
        }

        if (!opts.Tenancy.IsValid)
        {
            failures.Add("Gateway:Tenancy:Mode must be Single or Multi (empty means Multi).");
        }

        if (opts.Casbin.Enabled &&
            !string.IsNullOrWhiteSpace(opts.Casbin.ModelPath) &&
            !_fileSystem.FileExists(opts.Casbin.ModelPath))
        {
            failures.Add("Gateway:Casbin is enabled, but the configured ModelPath file was not found.");
        }

        if (opts.Casbin.Enabled && string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath))
        {
            failures.Add("Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.");
        }

        if (opts.Casbin.Enabled &&
            !string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath) &&
            !_fileSystem.FileExists(opts.Casbin.PolicyPath))
        {
            failures.Add("Gateway:Casbin is enabled, but the configured PolicyPath file was not found.");
        }

        if (!_environment.IsDevelopment() &&
            opts.OpenMetadata.Enabled &&
            !(Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) &&
              string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
            !opts.IsInsecureTransportAllowed)
        {
            failures.Add("Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.");
        }

        if (!_environment.IsDevelopment() &&
            !string.IsNullOrWhiteSpace(opts.Plugins.Directory) &&
            _fileSystem.DirectoryExists(opts.Plugins.Directory) &&
            !opts.Plugins.RequireIntegrityManifest)
        {
            failures.Add("Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
