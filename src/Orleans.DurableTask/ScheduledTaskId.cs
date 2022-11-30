namespace Orleans.Vesuvius;

public readonly struct ScheduledTaskId
{
    public static readonly ScheduledTaskId None = default;

    public ScheduledTaskId(string value)
    {
        Value = value;
    }

    public static implicit operator string(ScheduledTaskId id) => id.Value;
    public static implicit operator ScheduledTaskId(string value) => new (value);

    public string Value { get; }
}