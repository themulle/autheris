namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Fail-fast startup validator for <see cref="DocsOptions"/>.
/// </summary>
public sealed class DocsOptionsValidator : IValidateOptions<DocsOptions>
{
    public ValidateOptionsResult Validate(string? name, DocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MaxDocumentBytes <= 0)
        {
            failures.Add("Docs:MaxDocumentBytes must be greater than 0.");
        }

        if (options.MaxTitleLength <= 0)
        {
            failures.Add("Docs:MaxTitleLength must be greater than 0.");
        }

        if (options.MaxTags <= 0)
        {
            failures.Add("Docs:MaxTags must be greater than 0.");
        }

        if (options.MaxTagLength <= 0)
        {
            failures.Add("Docs:MaxTagLength must be greater than 0.");
        }

        if (options.MaxBundleBytes <= 0)
        {
            failures.Add("Docs:MaxBundleBytes must be greater than 0.");
        }

        if (options.MaxBundleEntries <= 0)
        {
            failures.Add("Docs:MaxBundleEntries must be greater than 0.");
        }

        if (options.MaxBundleUncompressedBytes <= 0)
        {
            failures.Add("Docs:MaxBundleUncompressedBytes must be greater than 0.");
        }

        if (options.TombstoneRetentionHours <= 0)
        {
            failures.Add("Docs:TombstoneRetentionHours must be greater than 0.");
        }

        if (options.Quota == null)
        {
            failures.Add("Docs:Quota must not be null.");
        }
        else
        {
            if (options.Quota.MaxDocumentsPerTenant <= 0)
            {
                failures.Add("Docs:Quota:MaxDocumentsPerTenant must be greater than 0.");
            }

            if (options.Quota.MaxBytesPerTenant <= 0)
            {
                failures.Add("Docs:Quota:MaxBytesPerTenant must be greater than 0.");
            }

            if (options.MaxDocumentBytes > options.Quota.MaxBytesPerTenant)
            {
                failures.Add("Docs:MaxDocumentBytes must not exceed Docs:Quota:MaxBytesPerTenant.");
            }
        }

        if (options.IndexRefreshSeconds is < 5 or > 600)
        {
            failures.Add("Docs:IndexRefreshSeconds must be between 5 and 600.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
