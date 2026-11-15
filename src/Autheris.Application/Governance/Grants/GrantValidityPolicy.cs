namespace Autheris.Application.Governance.Grants;

using System;
using System.Globalization;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Strict ISO-8601 duration parser supporting date and time components (<c>P[nY][nM][nW][nD][T[nH][nM][nS]]</c>).
/// Months are normalized to 30 days and years to 365 days; zero and negative durations are rejected.
/// </summary>
public static class Iso8601Duration
{
    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        if (span.Length < 3 || span[0] != 'P')
        {
            return false;
        }

        var idx = 1;
        var inTime = false;
        var hasComponent = false;
        long totalSeconds = 0;

        while (idx < span.Length)
        {
            if (span[idx] == 'T')
            {
                if (inTime || idx == span.Length - 1)
                {
                    return false;
                }

                inTime = true;
                idx++;
                continue;
            }

            var numStart = idx;
            while (idx < span.Length && char.IsAsciiDigit(span[idx]))
            {
                idx++;
            }

            if (idx == numStart || idx >= span.Length)
            {
                return false;
            }

            if (!long.TryParse(span[numStart..idx], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            {
                return false;
            }

            var unit = span[idx++];
            long multiplierSeconds = (inTime, unit) switch
            {
                (false, 'Y') => 365L * 86400L,
                (false, 'M') => 30L * 86400L,
                (false, 'W') => 7L * 86400L,
                (false, 'D') => 86400L,
                (true, 'H') => 3600L,
                (true, 'M') => 60L,
                (true, 'S') => 1L,
                _ => -1L
            };

            if (multiplierSeconds <= 0)
            {
                return false;
            }

            try
            {
                checked
                {
                    totalSeconds += amount * multiplierSeconds;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            hasComponent = true;
        }

        if (!hasComponent || totalSeconds <= 0)
        {
            return false;
        }

        try
        {
            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }
        catch (OverflowException)
        {
            duration = TimeSpan.Zero;
            return false;
        }
    }

    public static TimeSpan ParseOrThrow(string? value, string name)
    {
        if (!TryParse(value, out var duration))
        {
            throw new InvalidOperationException($"Grant validity setting '{name}' has invalid or missing ISO-8601 duration '{value ?? "<null>"}'.");
        }

        return duration;
    }
}

public sealed class GrantValidityException(string code, string message, int statusCode = 400) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Central policy bounding grant validity windows across request submission, approval decisions,
/// declarative manifests, and bulk grants.
/// </summary>
public interface IGrantValidityPolicy
{
    TimeSpan MaxDuration { get; }
    TimeSpan DefaultDuration { get; }
    TimeSpan DeclarativeMaxDuration { get; }

    DateTimeOffset ResolveRequestedValidTo(
        DateTimeOffset now,
        DateTimeOffset? requestedValidFrom,
        DateTimeOffset? requestedValidTo,
        int? durationDays = null,
        string? requestedDuration = null);

    DateTimeOffset ResolveApprovedValidTo(
        ConsentRequest request,
        DateTimeOffset? approverValidTo,
        DateTimeOffset now);

    void ValidateDeclarativeValidTo(
        DateTimeOffset? validUntil,
        DateTimeOffset now);

    void ValidateDurationDays(int days);
}

public sealed class GrantValidityPolicy : IGrantValidityPolicy
{
    public const string ValidityExceedsRequestedCode = "VALIDITY_EXCEEDS_REQUESTED";
    public const string ValidityExceedsMaximumCode = "VALIDITY_EXCEEDS_MAXIMUM";
    public const string RequestValidityElapsedCode = "REQUEST_VALIDITY_ELAPSED";

    public TimeSpan MaxDuration { get; }
    public TimeSpan DefaultDuration { get; }
    public TimeSpan DeclarativeMaxDuration { get; }

    public GrantValidityPolicy(IOptions<GrantValidityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value ?? throw new InvalidOperationException("GrantValidityOptions must not be null.");
        var validation = ValidateOptions(value);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                GrantValidityOptions.SectionName,
                typeof(GrantValidityOptions),
                validation.Failures);
        }

        MaxDuration = Iso8601Duration.ParseOrThrow(value.MaxDuration, nameof(GrantValidityOptions.MaxDuration));
        DefaultDuration = Iso8601Duration.ParseOrThrow(value.DefaultDuration, nameof(GrantValidityOptions.DefaultDuration));
        DeclarativeMaxDuration = string.IsNullOrWhiteSpace(value.DeclarativeMaxDuration)
            ? MaxDuration
            : Iso8601Duration.ParseOrThrow(value.DeclarativeMaxDuration, nameof(GrantValidityOptions.DeclarativeMaxDuration));
    }

    public static GrantValidityPolicy CreateDefault() =>
        new(Options.Create(GrantValidityOptions.CreateDefault()));

    internal static ValidateOptionsResult ValidateOptions(GrantValidityOptions options)
    {
        if (!Iso8601Duration.TryParse(options.MaxDuration, out var maxDuration))
        {
            return ValidateOptionsResult.Fail(
                $"'{GrantValidityOptions.SectionName}:{nameof(GrantValidityOptions.MaxDuration)}' must be a positive ISO-8601 duration (for example, 'P2M').");
        }

        if (!Iso8601Duration.TryParse(options.DefaultDuration, out var defaultDuration))
        {
            return ValidateOptionsResult.Fail(
                $"'{GrantValidityOptions.SectionName}:{nameof(GrantValidityOptions.DefaultDuration)}' must be a positive ISO-8601 duration (for example, 'P2W').");
        }

        if (defaultDuration > maxDuration)
        {
            return ValidateOptionsResult.Fail(
                $"'{GrantValidityOptions.SectionName}:{nameof(GrantValidityOptions.DefaultDuration)}' ({options.DefaultDuration}) cannot exceed '{nameof(GrantValidityOptions.MaxDuration)}' ({options.MaxDuration}).");
        }

        if (!string.IsNullOrWhiteSpace(options.DeclarativeMaxDuration) &&
            !Iso8601Duration.TryParse(options.DeclarativeMaxDuration, out _))
        {
            return ValidateOptionsResult.Fail(
                $"'{GrantValidityOptions.SectionName}:{nameof(GrantValidityOptions.DeclarativeMaxDuration)}' must be a positive ISO-8601 duration when configured.");
        }

        return ValidateOptionsResult.Success;
    }

    public DateTimeOffset ResolveRequestedValidTo(
        DateTimeOffset now,
        DateTimeOffset? requestedValidFrom,
        DateTimeOffset? requestedValidTo,
        int? durationDays = null,
        string? requestedDuration = null)
    {
        var effectiveStart = requestedValidFrom ?? now;
        DateTimeOffset target;

        if (requestedValidTo.HasValue)
        {
            target = requestedValidTo.Value;
        }
        else if (!string.IsNullOrWhiteSpace(requestedDuration))
        {
            if (!Iso8601Duration.TryParse(requestedDuration, out var parsed))
            {
                throw new GrantValidityException("VALIDATION_ERROR", $"Requested duration '{requestedDuration}' is not a valid positive ISO-8601 duration.");
            }

            target = effectiveStart.Add(parsed);
        }
        else if (durationDays.HasValue)
        {
            if (durationDays.Value < 1)
            {
                throw new GrantValidityException("VALIDATION_ERROR", "Duration must be at least 1 day.");
            }

            target = effectiveStart.AddDays(durationDays.Value);
        }
        else
        {
            target = effectiveStart.Add(DefaultDuration);
        }

        if (target <= now || target <= effectiveStart)
        {
            throw new GrantValidityException(GrantErrorCodes.ExpiryInPast, "Requested validity end must be in the future.");
        }

        if (target - effectiveStart > MaxDuration || target - now > MaxDuration)
        {
            throw new GrantValidityException(
                ValidityExceedsMaximumCode,
                $"Requested validity exceeds the maximum allowed duration of {MaxDuration.TotalDays:0.#} days.");
        }

        return target;
    }

    public DateTimeOffset ResolveApprovedValidTo(
        ConsentRequest request,
        DateTimeOffset? approverValidTo,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestedValidTo <= now)
        {
            throw new GrantValidityException(
                RequestValidityElapsedCode,
                "The requested validity window has already elapsed and can no longer be approved.",
                statusCode: 409);
        }

        var maxAllowed = now.Add(MaxDuration);
        if (approverValidTo.HasValue)
        {
            if (approverValidTo.Value <= now)
            {
                throw new GrantValidityException(
                    GrantErrorCodes.ExpiryInPast,
                    "The approved validity end must lie in the future.");
            }

            if (approverValidTo.Value > request.RequestedValidTo)
            {
                throw new GrantValidityException(
                    ValidityExceedsRequestedCode,
                    "The approved validity end cannot exceed the requester's requested validity.");
            }

            if (approverValidTo.Value > maxAllowed)
            {
                throw new GrantValidityException(
                    ValidityExceedsMaximumCode,
                    $"The approved validity end exceeds the maximum allowed duration of {MaxDuration.TotalDays:0.#} days.");
            }

            return approverValidTo.Value;
        }

        if (request.RequestedValidTo > maxAllowed)
        {
            throw new GrantValidityException(
                ValidityExceedsMaximumCode,
                $"The requested validity exceeds the maximum allowed duration of {MaxDuration.TotalDays:0.#} days.");
        }

        return request.RequestedValidTo;
    }

    public void ValidateDeclarativeValidTo(DateTimeOffset? validUntil, DateTimeOffset now)
    {
        if (!validUntil.HasValue)
        {
            throw new GrantPolicyViolationException(
                GrantDecision.Deny(
                    GrantErrorCodes.RequiresExpiry,
                    "Every declarative grant requires an explicit expiry (validUntil)."));
        }

        if (validUntil.Value <= now)
        {
            throw new GrantPolicyViolationException(
                GrantDecision.Deny(
                    GrantErrorCodes.ExpiryInPast,
                    "The expiry of the declarative grant must lie in the future."));
        }

        var maxValidTo = now.Add(DeclarativeMaxDuration);
        if (validUntil.Value > maxValidTo)
        {
            throw new GrantPolicyViolationException(
                GrantDecision.Deny(
                    GrantErrorCodes.ExceedsMaximum,
                    $"The requested expiry exceeds the maximum declarative duration of {DeclarativeMaxDuration.TotalDays:0.#} days.",
                    maxValidTo: maxValidTo));
        }
    }

    public void ValidateDurationDays(int days)
    {
        if (days < 1)
        {
            throw new GrantValidityException("VALIDATION_ERROR", "Duration must be at least 1 day.");
        }

        if (TimeSpan.FromDays(days) > MaxDuration)
        {
            throw new GrantValidityException(
                ValidityExceedsMaximumCode,
                $"Requested duration of {days} days exceeds the maximum allowed duration of {MaxDuration.TotalDays:0.#} days.");
        }
    }
}

public sealed class GrantValidityOptionsValidator : IValidateOptions<GrantValidityOptions>
{
    public ValidateOptionsResult Validate(string? name, GrantValidityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GrantValidityPolicy.ValidateOptions(options);
    }
}
