using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Validates high-availability and drain/shutdown timeout invariants.
/// </summary>
public sealed class GatewayResilienceOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();

        if (opts.HighAvailability.ShutdownTimeoutSeconds < opts.HighAvailability.QueryTimeoutSeconds + 10)
        {
            failures.Add("NF-HA-01 violation: ShutdownTimeoutSeconds must be at least 10s greater than QueryTimeoutSeconds.");
        }

        if (opts.HighAvailability.TerminationGracePeriodSeconds <
            opts.HighAvailability.DrainDelaySeconds + opts.HighAvailability.ShutdownTimeoutSeconds + 10)
        {
            failures.Add("NF-HA-01 violation: TerminationGracePeriodSeconds must be greater than DrainDelay + ShutdownTimeout + 10s.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
