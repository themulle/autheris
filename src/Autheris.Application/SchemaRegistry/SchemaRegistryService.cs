namespace Autheris.Application.SchemaRegistry;

using FluentValidation;
using Microsoft.Extensions.Logging;

public sealed class SchemaRegistryService : ISchemaRegistryService
{
    private readonly ISchemaRegistryRepository _repository;
    private readonly ISchemaLinter _linter;
    private readonly IValidator<SchemaRegistrationRequest> _validator;
    private readonly ILogger<SchemaRegistryService> _logger;

    public SchemaRegistryService(
        ISchemaRegistryRepository repository,
        ISchemaLinter linter,
        IValidator<SchemaRegistrationRequest> validator,
        ILogger<SchemaRegistryService> logger)
    {
        _repository = repository;
        _linter = linter;
        _validator = validator;
        _logger = logger;
    }

    public async Task<SchemaRegistrationResponse> RegisterSchemaAsync(
        SchemaRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errors = string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage));
            _logger.LogWarning("Schema registration validation failed for service '{Service}': {Errors}", request.ServiceName, errors);
            return new SchemaRegistrationResponse
            {
                Success = false,
                Message = $"Validation failed: {errors}",
                Diff = SchemaDiffResult.Empty
            };
        }

        var latest = await _repository.GetLatestAsync(request.ServiceName, cancellationToken);

        SchemaDiffResult diff;
        if (latest == null)
        {
            // Initial schema publication
            diff = SchemaDiffResult.Empty;
        }
        else
        {
            try
            {
                diff = _linter.Compare(latest.Sdl, request.Sdl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MISC-16: a baseline or target that cannot be compared is a validation failure, never a 500.
                _logger.LogWarning(ex, "Schema comparison failed for service '{Service}'.", request.ServiceName);
                return new SchemaRegistrationResponse
                {
                    Success = false,
                    Message = $"Schema comparison failed: {(ex is SchemaCompareException ? ex.Message : "the stored or submitted schema could not be compared.")}",
                    Diff = SchemaDiffResult.Empty,
                    ErrorCode = SchemaCompareException.ErrorCode
                };
            }
        }

        if (diff.HasBreakingChanges && !request.ForceIfBreaking)
        {
            _logger.LogWarning(
                "Schema registration rejected for service '{Service}': {Count} breaking changes detected.",
                request.ServiceName,
                diff.BreakingCount);

            return new SchemaRegistrationResponse
            {
                Success = false,
                Message = $"Schema check failed with {diff.BreakingCount} breaking change(s). Registration rejected.",
                Diff = diff
            };
        }

        if (request.DryRun)
        {
            return new SchemaRegistrationResponse
            {
                Success = true,
                Message = "Dry run schema validation and diff succeeded.",
                Diff = diff
            };
        }

        var version = request.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            var history = await _repository.GetHistoryAsync(request.ServiceName, cancellationToken);
            version = $"v{history.Count + 1}.0.0";
        }

        var registeredSchema = new RegisteredSchema
        {
            ServiceName = request.ServiceName,
            Version = version,
            Sdl = request.Sdl,
            RegisteredAt = DateTimeOffset.UtcNow,
            GitCommit = request.GitCommit,
            GitBranch = request.GitBranch,
            RegisteredBy = request.RegisteredBy ?? "CI/CD",
            IsActive = true
        };

        await _repository.SaveSchemaAsync(registeredSchema, cancellationToken);

        _logger.LogInformation(
            "Schema version '{Version}' successfully registered for service '{Service}'. (Changes: {Breaking} breaking, {Dangerous} dangerous, {Safe} safe)",
            version,
            request.ServiceName,
            diff.BreakingCount,
            diff.DangerousCount,
            diff.SafeCount);

        return new SchemaRegistrationResponse
        {
            Success = true,
            Message = $"Schema successfully registered as version '{version}'.",
            Schema = registeredSchema,
            Diff = diff
        };
    }

    public async Task<SchemaDiffResult> CheckSchemaAsync(
        string serviceName,
        string targetSdl,
        CancellationToken cancellationToken = default)
    {
        var latest = await _repository.GetLatestAsync(serviceName, cancellationToken);
        if (latest == null)
        {
            return SchemaDiffResult.Empty;
        }

        try
        {
            return _linter.Compare(latest.Sdl, targetSdl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Schema comparison failed for service '{Service}'.", serviceName);
            return SchemaDiffResult.FromChanges([SchemaChange.Breaking(SchemaCompareException.ErrorCode, serviceName, "The stored or submitted schema could not be compared.")]);
        }
    }

    public Task<RegisteredSchema?> GetLatestSchemaAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return _repository.GetLatestAsync(serviceName, cancellationToken);
    }

    public Task<IReadOnlyList<RegisteredSchema>> GetSchemaHistoryAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return _repository.GetHistoryAsync(serviceName, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetAllServicesAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetAllServicesAsync(cancellationToken);
    }
}
