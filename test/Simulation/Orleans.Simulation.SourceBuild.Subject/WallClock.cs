namespace Orleans.Simulation.SourceBuild.Subject;

/// <summary>Reads the wall clock, as production code does.</summary>
public static class WallClock
{
    /// <summary>Returns the current UTC time.</summary>
    public static DateTime UtcNow() => DateTime.UtcNow;
}
