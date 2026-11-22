namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>Finds files of this repository from the test binaries.</summary>
internal static class Repository
{
    // CI builds map source paths (ContinuousIntegrationBuild), so the repository is found from the test binaries.
    private static readonly Lazy<string> Root = new(() =>
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    });

    /// <summary>Returns the full path of a file given by its path segments from the repository root.</summary>
    public static string PathOf(params string[] segments) => Path.Combine([Root.Value, .. segments]);
}
