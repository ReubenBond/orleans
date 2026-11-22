using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation;

/// <summary>Builds unambiguous, versioned canonical encodings for signatures and cache keys.</summary>
internal sealed class CanonicalEncoding
{
    private const string FormatPrefix = "clockwork-canonical-v1;";
    private readonly StringBuilder _builder = new(FormatPrefix);

    public CanonicalEncoding(string type)
    {
        AddString("$type", type);
    }

    public void AddString(string name, string? value)
    {
        AddFieldName(name);
        AddStringValue(value);
    }

    public void AddBoolean(string name, bool value)
    {
        AddFieldName(name);
        _builder.Append(value ? "B1;" : "B0;");
    }

    public void AddInt32(string name, int value)
    {
        AddFieldName(name);
        _builder.Append('I').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
    }

    public void AddStringArray(string name, ImmutableArray<string> values)
    {
        AddFieldName(name);
        if (values.IsDefault)
        {
            _builder.Append("D;");
            return;
        }

        AddSequenceValues(values);
    }

    public void AddStringSequence(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        AddFieldName(name);
        AddSequenceValues(values.ToArray());
    }

    public override string ToString() => _builder.ToString();

    private void AddSequenceValues(IReadOnlyCollection<string> values)
    {
        _builder.Append('A')
            .Append(values.Count.ToString(CultureInfo.InvariantCulture))
            .Append(':');
        foreach (string? value in values)
        {
            AddStringValue(value);
        }

        _builder.Append(';');
    }

    private void AddFieldName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _builder.Append('F');
        AddLengthPrefixed(name);
    }

    private void AddStringValue(string? value)
    {
        if (value is null)
        {
            _builder.Append("N;");
            return;
        }

        _builder.Append('S');
        AddLengthPrefixed(value);
        _builder.Append(';');
    }

    private void AddLengthPrefixed(string value)
    {
        _builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}
