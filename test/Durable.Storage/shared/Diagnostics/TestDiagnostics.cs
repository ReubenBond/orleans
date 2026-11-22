using System.Reflection;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Locates and names the evidence a test run leaves behind (test/Durable.Storage/docs/spec/verification.md).
/// </summary>
internal static class TestDiagnostics
{
    private const int MaximumTestNameLength = 100;

    /// <summary>
    /// Gets or sets the run's results directory, which the platform extension records before any test runs. A test
    /// host which runs without it uses <c>TestResults</c> beside the test assembly.
    /// </summary>
    internal static string ResultsDirectory
    {
        get => Volatile.Read(ref field) ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Gets the test assembly's name.
    /// </summary>
    internal static string AssemblyName { get; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "tests";

    /// <summary>
    /// Gets the build configuration of the test assembly.
    /// </summary>
    internal static string Configuration { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";

    /// <summary>
    /// Gets a value indicating whether Stryker.NET runs this test application: <c>test/Durable.Storage/eng/mutation.ps1</c> sets
    /// <c>ClogMutationTesting</c> (test/Durable.Storage/docs/spec/verification.md §9).
    /// </summary>
    internal static bool UnderMutationTesting { get; } =
        string.Equals(Environment.GetEnvironmentVariable("ClogMutationTesting"), "true", StringComparison.Ordinal);

    /// <summary>
    /// Returns a path in the results directory named after the test and prefixed with the assembly name, so one
    /// directory can hold several projects' evidence.
    /// </summary>
    internal static string PathFor(string testName, string suffix)
    {
        var directory = ResultsDirectory;
        _ = Directory.CreateDirectory(directory);
        var name = new StringBuilder();
        if (!testName.StartsWith(AssemblyName, StringComparison.Ordinal))
        {
            _ = name.Append(AssemblyName).Append('_');
        }

        foreach (var character in testName.AsSpan(0, Math.Min(testName.Length, MaximumTestNameLength)))
        {
            _ = name.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return Path.Combine(directory, name.Append(suffix).ToString());
    }

    /// <summary>
    /// Writes a line to the current test's output when a test is running.
    /// </summary>
    internal static void WriteLine(string line)
    {
        try
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test which owned this output has finished, and its report is already written.
        }
    }
}
