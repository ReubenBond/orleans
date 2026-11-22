using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>Environment variables which control periodic simulation progress reporting.</summary>
public static class SimulationProgressEnvironment
{
    /// <summary>
    /// Enables progress output when set to a positive duration such as <c>5s</c>, <c>500ms</c>,
    /// <c>2m</c>, or <c>00:00:05</c>.
    /// </summary>
    public const string Interval = "ORLEANS_SIMULATION_PROGRESS_INTERVAL";

    internal static TimeSpan? GetInterval()
    {
        string? value = Environment.GetEnvironmentVariable(Interval);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TryParseInterval(value, out TimeSpan interval))
        {
            return interval;
        }

        throw new InvalidOperationException(
            $"{Interval} must be a positive duration such as '5s', '500ms', '2m', or '00:00:05', not '{value}'.");
    }

    internal static bool TryParseInterval(string? value, out TimeSpan interval)
    {
        interval = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.Contains(':') &&
            TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out interval) &&
            interval > TimeSpan.Zero)
        {
            return true;
        }

        (string Suffix, Func<double, TimeSpan> Convert)[] suffixes =
        [
            ("ms", TimeSpan.FromMilliseconds),
            ("s", TimeSpan.FromSeconds),
            ("m", TimeSpan.FromMinutes),
            ("h", TimeSpan.FromHours),
        ];

        foreach (var (suffix, convert) in suffixes)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) ||
                !double.IsFinite(amount) ||
                amount <= 0)
            {
                continue;
            }

            try
            {
                interval = convert(amount);
                return interval > TimeSpan.Zero;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return false;
    }
}
