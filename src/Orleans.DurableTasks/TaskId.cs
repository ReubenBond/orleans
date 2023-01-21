using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

[GenerateSerializer]
public readonly struct TaskId : ISpanFormattable, IEquatable<TaskId>, IParsable<TaskId>
{
    public static readonly TaskId None = default;

    [Id(0)]
    private readonly HierarchicalKey? _key;
    
    private TaskId(string value)
    {
        _key = HierarchicalKey.CreateEscaped(value);
    }
    
    private TaskId(HierarchicalKey key)
    {
        _key = key;
    }

    public static explicit operator string(TaskId taskId) => taskId.ToString();
    public static explicit operator TaskId(string taskId) => new(taskId);

    public override string ToString() => _key is null ? "" : _key.ToString();
    public override int GetHashCode() => _key is null ? 0 : _key.GetHashCode();
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_key is null)
        {
            charsWritten = 0;
            return true;
        }

        return _key.TryFormat(destination, out charsWritten, format, provider);
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => _key is null ? "" : _key.ToString(format, formatProvider);
    public override bool Equals(object? obj) => obj is TaskId id && Equals(id);
    public bool Equals(TaskId other) => _key is null && other._key is null || _key is not null && _key.Equals(other._key);

    public static TaskId Parse(string s, IFormatProvider? provider) => new(HierarchicalKey.Parse(s, provider));

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out TaskId result)
    {
        if (HierarchicalKey.TryParse(s, provider, out var key))
        {
            result = new TaskId(key);
            return true;
        }

        result = default;
        return false;
    }

    public static bool operator ==(TaskId left, TaskId right) => left.Equals(right);
    public static bool operator !=(TaskId left, TaskId right) => !left.Equals(right);
    public bool IsAncestorOf(TaskId other) => _key is not null && _key.IsPrefixOf(other._key);
    public bool IsDecendantOf(TaskId other) => other._key is not null && other._key.IsPrefixOf(_key);
    public bool IsParentOf(TaskId other) => _key is not null && _key.IsParentOf(other._key);
    public bool IsChildOf(TaskId other) => _key is not null && _key.IsChildOf(other._key);
    public TaskId Parent() => _key?.GetParent() is { } parent ? new(parent) : None;
    public TaskId Child(string value) => _key is { } key ? new(_key.CreateEscapedChildKey(value)) : new(value);
    public static TaskId Create(string value) => new(HierarchicalKey.CreateEscaped(value));
}

