using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

[GenerateSerializer]
public readonly struct TaskId : ISpanFormattable, IEquatable<TaskId>, IParsable<TaskId>
{
    public static readonly TaskId None = default;

    [Id(0)]
    private readonly HierarchicalKey? _key;
    
    public TaskId(string value)
    {
        _key = new HierarchicalKey(value);
    }
    
    public TaskId(TaskId parent, string value)
    {
        _key = new HierarchicalKey(parent._key, value);
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
    public override bool Equals(object? obj) => obj is TaskId && Equals((TaskId)obj);
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
}

