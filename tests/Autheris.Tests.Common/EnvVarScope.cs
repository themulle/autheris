namespace Autheris.Tests.Common;

/// <summary>Sets environment variables for the lifetime of the scope and restores the previous values on dispose.
/// Environment variables are process-global: tests using this must run in a serialised xUnit collection.</summary>
public sealed class EnvVarScope : IDisposable
{
    private readonly List<(string Name, string? Previous)> _saved = [];

    public EnvVarScope()
    {
    }

    public EnvVarScope(string name, string? value) => Set(name, value);

    public EnvVarScope(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            Set(name, value);
        }
    }

    /// <summary>Sets (or, with a null value, removes) a variable; the first previous value per name is what Dispose restores.</summary>
    public EnvVarScope Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_saved.Exists(s => s.Name == name))
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            Environment.SetEnvironmentVariable(_saved[i].Name, _saved[i].Previous);
        }

        _saved.Clear();
    }
}
