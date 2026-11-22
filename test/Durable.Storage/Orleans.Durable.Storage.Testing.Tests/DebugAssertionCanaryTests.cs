#define DEBUG

// Debug's calls compile only where DEBUG is defined, so this file defines it: the canaries fail assertions through Debug
// itself in every configuration, the Release tests' among them.
using System.Diagnostics;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// Runs canaries which raise debug assertions in a child test host, as xUnit and Microsoft.Testing.Platform run them,
/// and checks what the host reports: the test which swallowed an assertion fails alone, and an assertion outside every
/// test fails the test assembly at its cleanup (test/Durable.Storage/docs/spec/verification.md §11.2).
/// </summary>
/// <remarks>
/// The canaries are explicit, so ordinary runs leave them out; the child host runs one with <c>--explicit only</c>.
/// </remarks>
public sealed class DebugAssertionCanaryTests
{
    private const string Swallowed = "canary: caught in work the test started";
    private const string Outside = "canary: raised outside every test";
    private const string Uncaught = "canary: not caught";

    [Fact(Explicit = true)]
    public Task CanarySwallowsAnAssertionInWorkItStarts() => Task.Run(static () => Catch(Swallowed), TestContext.Current.CancellationToken);

    [Fact(Explicit = true)]
    public async Task CanaryRaisesAnAssertionOutsideEveryTest()
    {
        Task task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(static () => Catch(Outside), TestContext.Current.CancellationToken);
        }

        await task;
    }

    [Fact(Explicit = true)]
    public void CanaryRaisesAnAssertionItDoesNotCatch() => Debug.Fail(Uncaught);

    /// <summary>
    /// An assertion which a test swallows in work it started fails that test alone, with the assertion's message.
    /// </summary>
    [Fact]
    public async Task ASwallowedAssertionFailsItsTestAsTheHostRunsIt()
    {
        var run = await RunCanaryAsync(nameof(CanarySwallowsAnAssertionInWorkItStarts));

        Assert.True(run.ExitCode == 2, run.Output);
        var failed = Assert.Single(run.Tests(), static test => test.Result == "Fail");
        Assert.Equal(nameof(CanarySwallowsAnAssertionInWorkItStarts), failed.Method);
        Assert.Contains($"Debug assertion failed: {Swallowed}", failed.Message, StringComparison.Ordinal);
        Assert.Contains("1 debug assertion was recorded in this test, whether or not it propagated.", failed.Message, StringComparison.Ordinal);
        Assert.Empty(run.Errors());
    }

    /// <summary>
    /// An assertion raised outside every test's execution context fails the test assembly at its cleanup.
    /// </summary>
    [Fact]
    public async Task AnAssertionOutsideEveryTestFailsTheAssemblyAsTheHostRunsIt()
    {
        var run = await RunCanaryAsync(nameof(CanaryRaisesAnAssertionOutsideEveryTest));

        Assert.True(run.ExitCode == 2, run.Output);
        var error = Assert.Single(run.Errors());
        Assert.Equal("assembly-cleanup", error.Type);
        Assert.Contains($"Outside every test: {typeof(DebugAssertionException).FullName}: Debug assertion failed: {Outside}", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An assertion which propagates fails its test with the assertion, and again with the record of it, which says so.
    /// </summary>
    [Fact]
    public async Task AnUncaughtAssertionFailsItsTestAsTheHostRunsIt()
    {
        var run = await RunCanaryAsync(nameof(CanaryRaisesAnAssertionItDoesNotCatch));

        Assert.True(run.ExitCode == 2, run.Output);
        var failed = Assert.Single(run.Tests(), static test => test.Result == "Fail");
        Assert.Contains($"{typeof(DebugAssertionException).FullName} : Debug assertion failed: {Uncaught}", failed.Message, StringComparison.Ordinal);
        Assert.Contains("1 debug assertion was recorded in this test, whether or not it propagated.", failed.Message, StringComparison.Ordinal);
        Assert.Empty(run.Errors());
    }

    private static void Catch(string message)
    {
        try
        {
            Debug.Fail(message);
        }
        catch (DebugAssertionException)
        {
            // As code under test which counts a failure toward an outcome.
        }
    }

    /// <summary>
    /// Runs one canary in a child test host and returns its exit code, its output, and its xUnit report.
    /// </summary>
    private static async Task<CanaryRun> RunCanaryAsync(string canary)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var directory = TestDiagnostics.PathFor(TestContext.Current.Test!.TestDisplayName, ".canary");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        var start = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? $"{TestDiagnostics.AssemblyName}.exe" : TestDiagnostics.AssemblyName);
        if (File.Exists(host))
        {
            start.FileName = host;
        }
        else
        {
            start.FileName = Environment.ProcessPath!;
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, $"{TestDiagnostics.AssemblyName}.dll"));
        }

        string[] arguments =
        [
            "--explicit", "only",
            "--filter-method", $"{typeof(DebugAssertionCanaryTests).FullName}.{canary}",
            "--report-xunit-xml", "--report-xunit-xml-filename", "canary.xml",
            "--results-directory", directory,
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The child is a host of its own, not the test host controller's child or the test runner's.
        foreach (var variable in start.Environment.Keys.Where(static name => name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _ = start.Environment.Remove(variable);
        }

        start.Environment[FailureDumpWriter.LimitVariable] = "0";
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CanaryRun(process.ExitCode, await output + await error, XDocument.Load(Path.Combine(directory, "canary.xml")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed record CanaryRun(int ExitCode, string Output, XDocument Report)
    {
        public IEnumerable<(string Method, string Result, string Message)> Tests() =>
            Report.Descendants("test").Select(static test => (
                (string?)test.Attribute("method") ?? string.Empty,
                (string?)test.Attribute("result") ?? string.Empty,
                test.Element("failure")?.Element("message")?.Value ?? string.Empty));

        public IEnumerable<(string Type, string Message)> Errors() =>
            Report.Descendants("error").Select(static error => (
                (string?)error.Attribute("type") ?? string.Empty,
                error.Element("failure")?.Element("message")?.Value ?? string.Empty));
    }
}
