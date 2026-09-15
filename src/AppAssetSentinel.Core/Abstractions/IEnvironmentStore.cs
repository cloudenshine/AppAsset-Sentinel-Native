namespace AppAssetSentinel.Core.Abstractions;

public enum EnvironmentScope
{
    User,
    Process
}

/// <summary>
/// Indirection over environment-variable access so unit tests never write the real
/// user environment (AUDIT A09). Production uses <see cref="SystemEnvironmentStore"/>;
/// tests inject <see cref="InMemoryEnvironmentStore"/>.
/// </summary>
public interface IEnvironmentStore
{
    string? Get(string name, EnvironmentScope scope);

    /// <summary>Applies the value and returns the previous value so it can be restored (AUDIT A19).</summary>
    string? Set(string name, string value, EnvironmentScope scope);

    void Restore(string name, string? previousValue, EnvironmentScope scope);
}

public sealed class SystemEnvironmentStore : IEnvironmentStore
{
    public static readonly SystemEnvironmentStore Instance = new();

    public string? Get(string name, EnvironmentScope scope) =>
        Environment.GetEnvironmentVariable(name, scope == EnvironmentScope.User
            ? EnvironmentVariableTarget.User
            : EnvironmentVariableTarget.Process);

    public string? Set(string name, string value, EnvironmentScope scope)
    {
        var target = scope == EnvironmentScope.User
            ? EnvironmentVariableTarget.User
            : EnvironmentVariableTarget.Process;

        string? previous = Environment.GetEnvironmentVariable(name, target);
        Environment.SetEnvironmentVariable(name, value, target);
        return previous;
    }

    public void Restore(string name, string? previousValue, EnvironmentScope scope)
    {
        var target = scope == EnvironmentScope.User
            ? EnvironmentVariableTarget.User
            : EnvironmentVariableTarget.Process;

        Environment.SetEnvironmentVariable(name, previousValue, target);
    }
}

/// <summary>Test double: records every write in memory and leaves the host untouched.</summary>
public sealed class InMemoryEnvironmentStore : IEnvironmentStore
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string?> Snapshot => _values;

    public string? Get(string name, EnvironmentScope scope) =>
        _values.TryGetValue(Key(name, scope), out var v) ? v : null;

    public string? Set(string name, string value, EnvironmentScope scope)
    {
        string key = Key(name, scope);
        _values.TryGetValue(key, out string? previous);
        _values[key] = value;
        return previous;
    }

    public void Restore(string name, string? previousValue, EnvironmentScope scope)
    {
        _values[Key(name, scope)] = previousValue;
    }

    private static string Key(string name, EnvironmentScope scope) => $"{scope}:{name}";
}
