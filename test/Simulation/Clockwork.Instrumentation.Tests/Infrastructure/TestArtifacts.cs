namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Resolves the base directory under which slow, process-based tests stage their transient
/// artifacts (source closures, staged instrumented output, manifests, packed feeds).
/// </summary>
/// <remarks>
/// By default this is the machine temp directory. Setting <c>ORLEANS_SIMULATION_TEST_ARTIFACTS</c> redirects
/// it to a caller-controlled location so CI can retain manifests and transformed fixtures on failure
/// instead of losing them inside an ephemeral temp path.
/// </remarks>
internal static class TestArtifacts
{
    /// <summary>Gets the root directory for transient test artifacts.</summary>
    public static string Root
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("ORLEANS_SIMULATION_TEST_ARTIFACTS");
            return string.IsNullOrWhiteSpace(overridden) ? Path.GetTempPath() : overridden;
        }
    }

    /// <summary>Creates a unique subdirectory under <see cref="Root"/> for a single test's artifacts.</summary>
    /// <param name="category">A stable grouping name, for example <c>cwr-exec-tests</c>.</param>
    /// <returns>The absolute path of the freshly created directory.</returns>
    public static string CreateUnique(string category)
    {
        string path = Path.Combine(Root, category, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
